using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProxyId",
                table: "exchange_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "proxy_servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proxy_servers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_proxy_servers_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exchange_accounts_ProxyId",
                table: "exchange_accounts",
                column: "ProxyId");

            migrationBuilder.CreateIndex(
                name: "IX_proxy_servers_UserId",
                table: "proxy_servers",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_exchange_accounts_proxy_servers_ProxyId",
                table: "exchange_accounts",
                column: "ProxyId",
                principalTable: "proxy_servers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_exchange_accounts_proxy_servers_ProxyId",
                table: "exchange_accounts");

            migrationBuilder.DropTable(
                name: "proxy_servers");

            migrationBuilder.DropIndex(
                name: "IX_exchange_accounts_ProxyId",
                table: "exchange_accounts");

            migrationBuilder.DropColumn(
                name: "ProxyId",
                table: "exchange_accounts");
        }
    }
}
