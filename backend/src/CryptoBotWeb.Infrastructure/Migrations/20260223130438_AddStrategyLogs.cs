using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "strategy_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StrategyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_strategy_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_strategy_logs_strategies_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "strategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_strategy_logs_StrategyId_CreatedAt",
                table: "strategy_logs",
                columns: new[] { "StrategyId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "strategy_logs");
        }
    }
}
