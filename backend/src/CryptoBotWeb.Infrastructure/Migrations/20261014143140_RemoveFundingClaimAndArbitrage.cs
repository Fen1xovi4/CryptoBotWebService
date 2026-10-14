using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFundingClaimAndArbitrage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The FundingClaim and FuturesArbitrage strategies were removed from the code. Rows of
            // those types have no handler any more; drop them (trades/logs cascade) so they don't
            // linger as dead bots. Workspaces go after their strategies (FK is SetNull).
            migrationBuilder.Sql(
                "DELETE FROM strategies WHERE \"Type\" IN ('FundingClaim', 'FuturesArbitrage');");
            migrationBuilder.Sql(
                "DELETE FROM workspaces WHERE \"StrategyType\" IN ('FundingClaim', 'FuturesArbitrage');");

            migrationBuilder.DropForeignKey(
                name: "FK_strategies_exchange_accounts_SecondAccountId",
                table: "strategies");

            migrationBuilder.DropIndex(
                name: "IX_strategies_SecondAccountId",
                table: "strategies");

            migrationBuilder.DropColumn(
                name: "SecondAccountId",
                table: "strategies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SecondAccountId",
                table: "strategies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_strategies_SecondAccountId",
                table: "strategies",
                column: "SecondAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_strategies_exchange_accounts_SecondAccountId",
                table: "strategies",
                column: "SecondAccountId",
                principalTable: "exchange_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
