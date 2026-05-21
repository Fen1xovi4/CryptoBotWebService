using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TradeAccountFkCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_trades_exchange_accounts_AccountId",
                table: "trades");

            migrationBuilder.AddForeignKey(
                name: "FK_trades_exchange_accounts_AccountId",
                table: "trades",
                column: "AccountId",
                principalTable: "exchange_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_trades_exchange_accounts_AccountId",
                table: "trades");

            migrationBuilder.AddForeignKey(
                name: "FK_trades_exchange_accounts_AccountId",
                table: "trades",
                column: "AccountId",
                principalTable: "exchange_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
