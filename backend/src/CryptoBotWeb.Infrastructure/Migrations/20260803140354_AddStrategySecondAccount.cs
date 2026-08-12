using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategySecondAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
    }
}
