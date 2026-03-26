using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramBots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TelegramBotId",
                table: "strategies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "telegram_bots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BotToken = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Password = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_bots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_telegram_bots_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "telegram_subscribers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TelegramBotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubscribedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_subscribers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_telegram_subscribers_telegram_bots_TelegramBotId",
                        column: x => x.TelegramBotId,
                        principalTable: "telegram_bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_strategies_TelegramBotId",
                table: "strategies",
                column: "TelegramBotId");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_bots_UserId",
                table: "telegram_bots",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_subscribers_TelegramBotId_ChatId",
                table: "telegram_subscribers",
                columns: new[] { "TelegramBotId", "ChatId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_strategies_telegram_bots_TelegramBotId",
                table: "strategies",
                column: "TelegramBotId",
                principalTable: "telegram_bots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_strategies_telegram_bots_TelegramBotId",
                table: "strategies");

            migrationBuilder.DropTable(
                name: "telegram_subscribers");

            migrationBuilder.DropTable(
                name: "telegram_bots");

            migrationBuilder.DropIndex(
                name: "IX_strategies_TelegramBotId",
                table: "strategies");

            migrationBuilder.DropColumn(
                name: "TelegramBotId",
                table: "strategies");
        }
    }
}
