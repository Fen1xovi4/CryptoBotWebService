using CryptoBotWeb.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<StrategyLog> StrategyLogs => Set<StrategyLog>();

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
            e.Property(x => x.IsAdmin).HasDefaultValue(false);
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
    }
}
