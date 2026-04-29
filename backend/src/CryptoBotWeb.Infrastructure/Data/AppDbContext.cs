using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();
    public DbSet<ProxyServer> ProxyServers => Set<ProxyServer>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<StrategyLog> StrategyLogs => Set<StrategyLog>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<InviteCodeUsage> InviteCodeUsages => Set<InviteCodeUsage>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<PaymentWallet> PaymentWallets => Set<PaymentWallet>();
    public DbSet<PaymentSession> PaymentSessions => Set<PaymentSession>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<TelegramBot> TelegramBots => Set<TelegramBot>();
    public DbSet<TelegramSubscriber> TelegramSubscribers => Set<TelegramSubscriber>();
    public DbSet<SymbolBlacklistEntry> SymbolBlacklist => Set<SymbolBlacklistEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Username).HasMaxLength(50);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.Role).HasConversion<short>().HasDefaultValue(UserRole.User);
            e.Property(x => x.IsEnabled).HasDefaultValue(true);
            e.Property(x => x.TwoFactorEnabled).HasDefaultValue(false);
            e.Property(x => x.TwoFactorSecret).HasMaxLength(512);
            e.Ignore(x => x.IsAdmin);
            e.HasOne(x => x.InvitedByUser)
                .WithMany()
                .HasForeignKey(x => x.InvitedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProxyServer>(e =>
        {
            e.ToTable("proxy_servers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(100);
            e.HasOne(x => x.User)
                .WithMany(u => u.ProxyServers)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExchangeAccount>(e =>
        {
            e.ToTable("exchange_accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.ExchangeType).HasConversion<short>();
            e.Property(x => x.DzengiAccountId).HasMaxLength(64);
            e.HasOne(x => x.User)
                .WithMany(u => u.ExchangeAccounts)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Proxy)
                .WithMany(p => p.ExchangeAccounts)
                .HasForeignKey(x => x.ProxyId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Strategy>(e =>
        {
            e.ToTable("strategies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Type).HasMaxLength(50);
            e.Property(x => x.ConfigJson).HasColumnType("jsonb");
            e.Property(x => x.StateJson).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<short>();
            e.HasOne(x => x.Account)
                .WithMany(a => a.Strategies)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Workspace)
                .WithMany(w => w.Strategies)
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.TelegramBot)
                .WithMany(t => t.Strategies)
                .HasForeignKey(x => x.TelegramBotId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Workspace>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.StrategyType).HasMaxLength(50);
            e.Property(x => x.ConfigJson).HasColumnType("jsonb");
            e.HasOne(x => x.User)
                .WithMany(u => u.Workspaces)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Trade>(e =>
        {
            e.ToTable("trades");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ExchangeOrderId).HasMaxLength(100);
            e.Property(x => x.Symbol).HasMaxLength(30);
            e.Property(x => x.Side).HasMaxLength(4);
            e.Property(x => x.Quantity).HasPrecision(18, 8);
            e.Property(x => x.Price).HasPrecision(18, 8);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.PnlDollar).HasPrecision(18, 8);
            e.Property(x => x.Commission).HasPrecision(18, 8);
            e.Property(x => x.FundingPnl).HasPrecision(18, 8);
            e.HasOne(x => x.Strategy)
                .WithMany(s => s.Trades)
                .HasForeignKey(x => x.StrategyId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account)
                .WithMany(a => a.Trades)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StrategyLog>(e =>
        {
            e.ToTable("strategy_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Level).HasMaxLength(10);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.HasOne(x => x.Strategy)
                .WithMany(s => s.Logs)
                .HasForeignKey(x => x.StrategyId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.StrategyId, x.CreatedAt });
        });

        modelBuilder.Entity<InviteCode>(e =>
        {
            e.ToTable("invite_codes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Code).HasMaxLength(20);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.AssignedRole).HasConversion<short>();
            e.Property(x => x.MaxUses).HasDefaultValue(1);
            e.Property(x => x.UsedCount).HasDefaultValue(0);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasOne(x => x.CreatedByUser)
                .WithMany(u => u.InviteCodes)
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InviteCodeUsage>(e =>
        {
            e.ToTable("invite_code_usages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasOne(x => x.InviteCode)
                .WithMany(c => c.Usages)
                .HasForeignKey(x => x.InviteCodeId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
                .WithMany(u => u.InviteCodeUsages)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.InviteCodeId, x.UserId });
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Plan).HasConversion<short>().HasDefaultValue(SubscriptionPlan.Basic);
            e.Property(x => x.Status).HasConversion<short>().HasDefaultValue(SubscriptionStatus.Active);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne(x => x.User)
                .WithOne(u => u.Subscription)
                .HasForeignKey<Subscription>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AssignedByUser)
                .WithMany()
                .HasForeignKey(x => x.AssignedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PaymentWallet>(e =>
        {
            e.ToTable("payment_wallets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.AddressTrc20).HasMaxLength(100);
            e.Property(x => x.AddressBep20).HasMaxLength(100);
            e.Property(x => x.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<PaymentSession>(e =>
        {
            e.ToTable("payment_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Plan).HasConversion<short>();
            e.Property(x => x.Network).HasConversion<short>();
            e.Property(x => x.Token).HasConversion<short>();
            e.Property(x => x.ExpectedAmount).HasPrecision(18, 2);
            e.Property(x => x.ReceivedAmount).HasPrecision(18, 6);
            e.Property(x => x.Status).HasConversion<short>().HasDefaultValue(PaymentSessionStatus.Pending);
            e.Property(x => x.TxHash).HasMaxLength(200);
            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Wallet)
                .WithMany(w => w.PaymentSessions)
                .HasForeignKey(x => x.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ConfirmedByAdmin)
                .WithMany()
                .HasForeignKey(x => x.ConfirmedByAdminId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AssignedInviteCode)
                .WithMany()
                .HasForeignKey(x => x.AssignedInviteCodeId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.WalletId, x.Status });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.GuestToken);
        });

        modelBuilder.Entity<SupportTicket>(e =>
        {
            e.ToTable("support_tickets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Subject).HasMaxLength(255);
            e.Property(x => x.Status).HasConversion<short>().HasDefaultValue(TicketStatus.Open);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.User)
                .WithMany(u => u.SupportTickets)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SupportMessage>(e =>
        {
            e.ToTable("support_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Text).HasMaxLength(4000);
            e.HasIndex(x => x.TicketId);
            e.HasOne(x => x.Ticket)
                .WithMany(t => t.Messages)
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sender)
                .WithMany()
                .HasForeignKey(x => x.SenderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TelegramBot>(e =>
        {
            e.ToTable("telegram_bots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.BotToken).HasMaxLength(256);
            e.Property(x => x.Password).HasMaxLength(100);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasOne(x => x.User)
                .WithMany(u => u.TelegramBots)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TelegramSubscriber>(e =>
        {
            e.ToTable("telegram_subscribers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Username).HasMaxLength(100);
            e.HasOne(x => x.TelegramBot)
                .WithMany(b => b.Subscribers)
                .HasForeignKey(x => x.TelegramBotId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TelegramBotId, x.ChatId }).IsUnique();
        });

        modelBuilder.Entity<SymbolBlacklistEntry>(e =>
        {
            e.ToTable("symbol_blacklist");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ExchangeType).HasConversion<short>();
            e.Property(x => x.Symbol).HasMaxLength(30);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.ExchangeType, x.Symbol }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}
