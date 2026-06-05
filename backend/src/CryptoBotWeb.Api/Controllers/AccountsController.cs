using System.Security.Claims;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly IExchangeServiceFactory _exchangeFactory;
    private readonly IProxyHealthTracker _proxyHealth;

    public AccountsController(AppDbContext db, IEncryptionService encryption,
        IExchangeServiceFactory exchangeFactory, IProxyHealthTracker proxyHealth)
    {
        _db = db;
        _encryption = encryption;
        _exchangeFactory = exchangeFactory;
        _proxyHealth = proxyHealth;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsAdmin() => User.IsInRole("Admin");

    [HttpGet]
    public async Task<ActionResult<List<ExchangeAccountDto>>> GetAll([FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var accounts = await _db.ExchangeAccounts
            .Where(a => a.UserId == targetUserId)
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ExchangeAccountDto
            {
                Id = a.Id,
                Name = a.Name,
                ExchangeType = a.ExchangeType,
                Proxies = a.AccountProxies
                    .OrderBy(ap => ap.Priority)
                    .Select(ap => new AccountProxyDto
                    {
                        ProxyId = ap.ProxyId,
                        Name = ap.Proxy.Name,
                        Priority = ap.Priority
                    }).ToList(),
                IsActive = a.IsActive,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(accounts);
    }

    [HttpPost]
    public async Task<ActionResult<ExchangeAccountDto>> Create([FromBody] CreateExchangeAccountRequest request)
    {
        var userId = GetUserId();

        // Subscription limit check
        if (!IsAdmin())
        {
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);
            var limits = PlanLimits.GetLimits(sub?.Plan ?? SubscriptionPlan.Basic);
            var currentCount = await _db.ExchangeAccounts.CountAsync(a => a.UserId == userId);
            if (currentCount >= limits.MaxAccounts)
                return StatusCode(403, new { message = $"Account limit reached ({currentCount}/{limits.MaxAccounts}). Upgrade your plan to add more." });
        }

        // Ordered, de-duplicated proxy list (index = failover priority, first = primary)
        var proxyIds = (request.ProxyIds ?? new List<Guid>()).Distinct().ToList();

        // Non-admin users must provide at least one proxy
        if (!IsAdmin() && proxyIds.Count == 0)
            return BadRequest(new { message = "Proxy is required. Please add a proxy first." });

        // Validate every proxy belongs to the user; capture names for the response
        var ownedProxies = await _db.ProxyServers
            .Where(p => proxyIds.Contains(p.Id) && p.UserId == userId)
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        if (proxyIds.Any(pid => !ownedProxies.ContainsKey(pid)))
            return BadRequest(new { message = "Invalid proxy selected." });

        var account = new ExchangeAccount
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            ExchangeType = request.ExchangeType,
            ApiKeyEncrypted = _encryption.Encrypt(request.ApiKey),
            ApiSecretEncrypted = _encryption.Encrypt(request.ApiSecret),
            PassphraseEncrypted = request.Passphrase != null ? _encryption.Encrypt(request.Passphrase) : null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            AccountProxies = proxyIds.Select((pid, idx) => new ExchangeAccountProxy
            {
                Id = Guid.NewGuid(),
                ProxyId = pid,
                Priority = idx
            }).ToList()
        };

        _db.ExchangeAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Auto-fetch DzengiAccountId on creation (one call, best-effort).
        if (account.ExchangeType == ExchangeType.Dzengi)
        {
            await TryFetchDzengiAccountIdAsync(account);
        }

        return CreatedAtAction(nameof(GetAll), new ExchangeAccountDto
        {
            Id = account.Id,
            Name = account.Name,
            ExchangeType = account.ExchangeType,
            Proxies = proxyIds.Select((pid, idx) => new AccountProxyDto
            {
                ProxyId = pid,
                Name = ownedProxies[pid],
                Priority = idx
            }).ToList(),
            IsActive = account.IsActive,
            CreatedAt = account.CreatedAt
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExchangeAccountRequest request)
    {
        var userId = GetUserId();
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);

        if (account == null)
            return NotFound();

        if (request.Name != null) account.Name = request.Name;
        if (request.ApiKey != null) account.ApiKeyEncrypted = _encryption.Encrypt(request.ApiKey);
        if (request.ApiSecret != null) account.ApiSecretEncrypted = _encryption.Encrypt(request.ApiSecret);
        if (request.Passphrase != null) account.PassphraseEncrypted = _encryption.Encrypt(request.Passphrase);
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;

        // Replace the proxy list when ProxyIds is provided (null = leave unchanged).
        // Empty list clears all proxies (admin only); non-admin must keep at least one.
        if (request.ProxyIds != null)
        {
            var proxyIds = request.ProxyIds.Distinct().ToList();

            if (!IsAdmin() && proxyIds.Count == 0)
                return BadRequest(new { message = "Proxy is required." });

            if (proxyIds.Count > 0)
            {
                var ownedCount = await _db.ProxyServers
                    .CountAsync(p => proxyIds.Contains(p.Id) && p.UserId == userId);
                if (ownedCount != proxyIds.Count)
                    return BadRequest(new { message = "Invalid proxy selected." });
            }

            _db.ExchangeAccountProxies.RemoveRange(account.AccountProxies);
            _db.ExchangeAccountProxies.AddRange(proxyIds.Select((pid, idx) => new ExchangeAccountProxy
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                ProxyId = pid,
                Priority = idx
            }));
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        _db.ExchangeAccounts.Remove(account);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> TestConnection(Guid id)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        // For Dzengi accounts without a cached accountId, fetch and persist it before testing.
        if (account.ExchangeType == ExchangeType.Dzengi && string.IsNullOrEmpty(account.DzengiAccountId))
        {
            try { await TryFetchDzengiAccountIdAsync(account); } catch { /* best-effort */ }
        }

        // Try each proxy in failover order; the first that connects wins. A network failure
        // (timeout / proxy down) rotates to the next proxy and is recorded in the health tracker.
        var proxies = account.OrderedProxies.ToList();
        var candidates = proxies.Count > 0 ? proxies.Select(p => (ProxyServer?)p).ToList() : new List<ProxyServer?> { null };
        string? lastError = null;

        foreach (var proxy in candidates)
        {
            try
            {
                // Fast TCP precheck first so a dead proxy is skipped in ~1.5 s instead of
                // hanging on the full exchange timeout (same mechanism the worker uses).
                if (proxy != null && !await _proxyHealth.PrecheckAsync(proxy))
                {
                    lastError = $"proxy {proxy.Name} unreachable (timed out)";
                    continue;
                }

                using var service = (IDisposable)_exchangeFactory.CreateWithProxy(account, proxy);
                var exchangeService = (IExchangeService)service;
                var (success, error) = await exchangeService.TestConnectionAsync();
                if (success)
                {
                    if (proxy != null) _proxyHealth.ReportSuccess(proxy.Id);
                    var via = proxy != null ? $" (via {proxy.Name})" : "";
                    return Ok(new { success = true, message = $"Connection successful{via}" });
                }
                lastError = error;
                if (proxy != null && IsNetworkError(error)) _proxyHealth.ReportFailure(proxy.Id);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (proxy != null) _proxyHealth.ReportFailure(proxy.Id);
            }
        }

        var prefix = proxies.Count > 1 ? $"All {proxies.Count} proxies failed" : "Connection failed";
        return Ok(new { success = false, message = $"{prefix}: {lastError}" });
    }

    // Network-class failures (timeout / unreachable proxy) — these justify failover.
    // Auth/signature/permission errors do NOT, so they must not rotate proxies.
    private static bool IsNetworkError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        var e = error.ToLowerInvariant();
        return e.Contains("timeout") || e.Contains("timed out") || e.Contains("webexception")
            || e.Contains("connection") || e.Contains("unreachable") || e.Contains("refused")
            || e.Contains("no such host") || e.Contains("proxy");
    }

    [HttpGet("{id:guid}/balance")]
    public async Task<IActionResult> GetBalance(Guid id)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            if (account.ExchangeType == ExchangeType.Dzengi && string.IsNullOrEmpty(account.DzengiAccountId))
            {
                await TryFetchDzengiAccountIdAsync(account);
            }

            using var service = (IDisposable)_exchangeFactory.Create(account);
            var exchangeService = (IExchangeService)service;
            var balances = await exchangeService.GetBalancesAsync();
            return Ok(new AccountBalanceResponse
            {
                AccountId = account.Id,
                AccountName = account.Name,
                Exchange = account.ExchangeType.ToString(),
                Balances = balances
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/positions")]
    public async Task<IActionResult> GetPositions(Guid id)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            if (account.ExchangeType == ExchangeType.Dzengi && string.IsNullOrEmpty(account.DzengiAccountId))
            {
                await TryFetchDzengiAccountIdAsync(account);
            }

            using var service = _exchangeFactory.CreateFutures(account);
            var positions = await service.GetOpenPositionsAsync();
            return Ok(positions);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/positions/close")]
    public async Task<IActionResult> ClosePosition(Guid id, [FromBody] ClosePositionRequest request)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Symbol) || string.IsNullOrWhiteSpace(request.Side))
            return BadRequest(new { message = "Symbol and side are required." });

        var side = request.Side.Trim().ToLowerInvariant();
        if (side != "long" && side != "short")
            return BadRequest(new { message = "Side must be 'long' or 'short'." });

        try
        {
            if (account.ExchangeType == ExchangeType.Dzengi && string.IsNullOrEmpty(account.DzengiAccountId))
            {
                await TryFetchDzengiAccountIdAsync(account);
            }

            using var service = _exchangeFactory.CreateFutures(account);

            // Re-fetch the live position so we close the exact remaining quantity, not stale UI data.
            var position = await service.GetPositionAsync(request.Symbol, side);
            if (position == null || position.Quantity <= 0)
                return BadRequest(new { message = "No open position found on the exchange for this symbol/side." });

            var result = side == "long"
                ? await service.CloseLongAsync(request.Symbol, position.Quantity)
                : await service.CloseShortAsync(request.Symbol, position.Quantity);

            if (!result.Success)
                return StatusCode(502, new { message = result.ErrorMessage ?? "Close failed" });

            return Ok(new
            {
                success = true,
                orderId = result.OrderId,
                filledPrice = result.FilledPrice,
                filledQuantity = result.FilledQuantity
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
    }

    private async Task TryFetchDzengiAccountIdAsync(ExchangeAccount account)
    {
        var service = _exchangeFactory.Create(account);
        try
        {
            if (service is DzengiExchangeService dzengi)
            {
                var accountId = await dzengi.FetchPrimaryAccountIdAsync();
                if (!string.IsNullOrEmpty(accountId))
                {
                    account.DzengiAccountId = accountId;
                    await _db.SaveChangesAsync();
                }
            }
        }
        catch
        {
            // Best-effort: if fetch fails, user re-runs /test later to fill the id.
        }
        finally
        {
            (service as IDisposable)?.Dispose();
        }
    }
}
