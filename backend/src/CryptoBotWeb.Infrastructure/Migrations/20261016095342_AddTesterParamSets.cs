using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTesterParamSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tester_param_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ExchangeType = table.Column<short>(type: "smallint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StrategyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                    SweptParamsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PeriodFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PeriodToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PathTimeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    NetPnlUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxDrawdownUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxDrawdownPercent = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    WinRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    TotalTrades = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tester_param_sets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tester_param_sets_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tester_param_sets_CreatedByUserId",
                table: "tester_param_sets",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tester_param_sets_ExchangeType_Symbol_StrategyType",
                table: "tester_param_sets",
                columns: new[] { "ExchangeType", "Symbol", "StrategyType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tester_param_sets");
        }
    }
}
