using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Services;

public class SymbolBlacklistService : ISymbolBlacklistService
{
    private static readonly TimeSpan TtlDuration = TimeSpan.FromDays(3);

    private readonly AppDbContext _db;
    private readonly ILogger<SymbolBlacklistService> _logger;

    public SymbolBlacklistService(AppDbContext db, ILogger<SymbolBlacklistService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AddOrRefreshAsync(ExchangeType exchangeType, string symbol, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        var normalized = symbol.ToUpperInvariant();
        var expiresAt = DateTime.UtcNow + TtlDuration;

        var existing = await _db.SymbolBlacklist
            .FirstOrDefaultAsync(e => e.ExchangeType == exchangeType && e.Symbol == normalized, ct);

        if (existing != null)
        {
            existing.ExpiresAt = expiresAt;
            if (!string.IsNullOrEmpty(reason))
                existing.Reason = reason;
        }
        else
        {
            _db.SymbolBlacklist.Add(new SymbolBlacklistEntry
            {
                Id = Guid.NewGuid(),
                ExchangeType = exchangeType,
                Symbol = normalized,
                ExpiresAt = expiresAt,
                Reason = reason,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Symbol blacklisted: {Exchange}/{Symbol} until {ExpiresAt:u}. Reason: {Reason}",
            exchangeType, normalized, expiresAt, reason ?? "(none)");
    }

    public async Task<HashSet<string>> GetActiveSetAsync(ExchangeType exchangeType, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var symbols = await _db.SymbolBlacklist
            .Where(e => e.ExchangeType == exchangeType && e.ExpiresAt > now)
            .Select(e => e.Symbol)
            .ToListAsync(ct);

        return new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
    }
}
