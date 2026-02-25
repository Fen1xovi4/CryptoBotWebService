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
    }
}
