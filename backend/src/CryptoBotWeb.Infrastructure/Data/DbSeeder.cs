using CryptoBotWeb.Core.Entities;
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
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        logger.LogInformation("Admin user '{Username}' seeded", adminUser);
    }
}
