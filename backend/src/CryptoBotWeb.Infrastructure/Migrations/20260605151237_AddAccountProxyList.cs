using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountProxyList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Create the join table first.
            migrationBuilder.CreateTable(
                name: "exchange_account_proxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProxyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_account_proxies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_exchange_account_proxies_exchange_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "exchange_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_exchange_account_proxies_proxy_servers_ProxyId",
                        column: x => x.ProxyId,
                        principalTable: "proxy_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exchange_account_proxies_AccountId",
                table: "exchange_account_proxies",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_exchange_account_proxies_AccountId_ProxyId",
                table: "exchange_account_proxies",
                columns: new[] { "AccountId", "ProxyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exchange_account_proxies_ProxyId",
                table: "exchange_account_proxies",
                column: "ProxyId");

            // 2) Migrate existing single-proxy assignments to priority 0 (primary).
            migrationBuilder.Sql(@"
                INSERT INTO exchange_account_proxies (""Id"", ""AccountId"", ""ProxyId"", ""Priority"")
                SELECT gen_random_uuid(), ""Id"", ""ProxyId"", 0
                FROM exchange_accounts
                WHERE ""ProxyId"" IS NOT NULL;");

            // 3) Drop the old single-proxy column now that data is copied.
            migrationBuilder.DropForeignKey(
                name: "FK_exchange_accounts_proxy_servers_ProxyId",
                table: "exchange_accounts");

            migrationBuilder.DropIndex(
                name: "IX_exchange_accounts_ProxyId",
                table: "exchange_accounts");

            migrationBuilder.DropColumn(
                name: "ProxyId",
                table: "exchange_accounts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_account_proxies");

            migrationBuilder.AddColumn<Guid>(
                name: "ProxyId",
                table: "exchange_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_exchange_accounts_ProxyId",
                table: "exchange_accounts",
                column: "ProxyId");

            migrationBuilder.AddForeignKey(
                name: "FK_exchange_accounts_proxy_servers_ProxyId",
                table: "exchange_accounts",
                column: "ProxyId",
                principalTable: "proxy_servers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
