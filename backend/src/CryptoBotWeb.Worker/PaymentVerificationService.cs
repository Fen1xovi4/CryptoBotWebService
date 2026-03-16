using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBotWeb.Worker;

public class PaymentVerificationService : BackgroundService
{
    private readonly ILogger<PaymentVerificationService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public PaymentVerificationService(ILogger<PaymentVerificationService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Payment verification service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingPayments(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in payment verification loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(BlockchainContracts.VerificationIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Payment verification service stopped");
    }

    private async Task ProcessPendingPayments(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tronService = scope.ServiceProvider.GetRequiredService<TronGridService>();
        var bscService = scope.ServiceProvider.GetRequiredService<BscScanService>();

        var pendingSessions = await db.PaymentSessions
            .Include(s => s.Wallet)
            .Where(s => s.Status == PaymentSessionStatus.Pending)
            .ToListAsync(ct);

        if (pendingSessions.Count == 0)
            return;

        _logger.LogDebug("Processing {Count} pending payment sessions", pendingSessions.Count);

        foreach (var session in pendingSessions)
        {
            try
            {
                if (session.ExpiresAt < DateTime.UtcNow)
                {
                    session.Status = PaymentSessionStatus.Expired;
                    _logger.LogInformation("Payment session {SessionId} expired", session.Id);
                    continue;
                }

                var walletAddress = session.Network == PaymentNetwork.TRC20
                    ? session.Wallet.AddressTrc20
                    : session.Wallet.AddressBep20;

                var contractAddress = GetContractAddress(session.Network, session.Token);

                IBlockchainService blockchainService = session.Network == PaymentNetwork.TRC20
                    ? tronService
                    : bscService;

                var transfer = await blockchainService.FindTransferAsync(
                    walletAddress, contractAddress, session.ExpectedAmount, session.CreatedAt, ct);

                if (transfer is null)
                    continue;

                session.Status = PaymentSessionStatus.Confirmed;
                session.TxHash = transfer.TxHash;
                session.ReceivedAmount = transfer.Amount;
                session.ConfirmedAt = DateTime.UtcNow;

                if (session.UserId is null)
                {
                    // Guest payment session — assign an available invite code
                    var now = DateTime.UtcNow;
                    var inviteCode = await db.InviteCodes
                        .Where(c => c.IsActive &&
                            (c.ExpiresAt == null || c.ExpiresAt > now) &&
                            (c.MaxUses == 0 || c.UsedCount < c.MaxUses) &&
                            !db.PaymentSessions.Any(ps =>
                                ps.AssignedInviteCodeId == c.Id &&
                                (ps.Status == PaymentSessionStatus.Confirmed || ps.Status == PaymentSessionStatus.ManuallyConfirmed)))
                        .FirstOrDefaultAsync(ct);

                    if (inviteCode != null)
                    {
                        session.AssignedInviteCodeId = inviteCode.Id;
                    }
                    else
                    {
                        _logger.LogWarning("No available invite codes for guest payment session {SessionId}", session.Id);
                    }

                    _logger.LogInformation(
                        "Guest payment confirmed for session {SessionId}, tx {TxHash}, amount {Amount}, inviteCodeId {InviteCodeId}",
                        session.Id, transfer.TxHash, transfer.Amount, session.AssignedInviteCodeId);
                }
                else
                {
                    // Registered user — upsert subscription
                    var subscription = await db.Subscriptions
                        .FirstOrDefaultAsync(s => s.UserId == session.UserId, ct);

                    if (subscription is null)
                    {
                        subscription = new Subscription
                        {
                            UserId = session.UserId.Value,
                            Plan = session.Plan,
                            Status = SubscriptionStatus.Active,
                            StartedAt = DateTime.UtcNow,
                            ExpiresAt = DateTime.UtcNow.AddMonths(1)
                        };
                        db.Subscriptions.Add(subscription);
                    }
                    else
                    {
                        var samePlan = subscription.Plan == session.Plan;
                        var stillActive = subscription.Status == SubscriptionStatus.Active
                                         && subscription.ExpiresAt.HasValue
                                         && subscription.ExpiresAt.Value > DateTime.UtcNow;

                        // Same plan + active → extend from current end date
                        // Different plan or expired → reset from now
                        var baseDate = samePlan && stillActive
                            ? subscription.ExpiresAt!.Value
                            : DateTime.UtcNow;

                        subscription.Plan = session.Plan;
                        subscription.Status = SubscriptionStatus.Active;
                        subscription.StartedAt = DateTime.UtcNow;
                        subscription.ExpiresAt = baseDate.AddMonths(1);
                    }

                    _logger.LogInformation(
                        "Payment confirmed for session {SessionId}, user {UserId}, tx {TxHash}, amount {Amount}",
                        session.Id, session.UserId, transfer.TxHash, transfer.Amount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing payment session {SessionId}", session.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string GetContractAddress(PaymentNetwork network, PaymentToken token)
    {
        return (network, token) switch
        {
            (PaymentNetwork.TRC20, PaymentToken.USDT) => BlockchainContracts.TRC20_USDT,
            (PaymentNetwork.TRC20, PaymentToken.USDC) => BlockchainContracts.TRC20_USDC,
            (PaymentNetwork.BEP20, PaymentToken.USDT) => BlockchainContracts.BEP20_USDT,
            (PaymentNetwork.BEP20, PaymentToken.USDC) => BlockchainContracts.BEP20_USDC,
            _ => throw new ArgumentOutOfRangeException($"Unsupported network/token combination: {network}/{token}")
        };
    }
}
