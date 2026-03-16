using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, string adminUser, string adminPassword, ILogger logger)
    {
        await db.Database.MigrateAsync();

        if (await db.Users.AnyAsync())
        {
            logger.LogInformation("Database already seeded");
            return;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = adminUser,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            Role = UserRole.Admin,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);

        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Plan = SubscriptionPlan.Pro,
            Status = SubscriptionStatus.Active,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });

        for (int i = 1; i <= 10; i++)
        {
            db.PaymentWallets.Add(new PaymentWallet
            {
                Id = Guid.NewGuid(),
                AddressTrc20 = $"TRC20_WALLET_{i}_REPLACE_ME",
                AddressBep20 = $"BEP20_WALLET_{i}_REPLACE_ME",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Admin user '{Username}' seeded with Pro subscription and 10 payment wallets", adminUser);
    }
}
