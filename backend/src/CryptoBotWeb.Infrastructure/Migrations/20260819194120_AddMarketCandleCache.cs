using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketCandleCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_candle_ranges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ExchangeType = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DownloadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_candle_ranges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_candles",
                columns: table => new
                {
                    ExchangeType = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    OpenTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    High = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    Volume = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_candles", x => new { x.ExchangeType, x.Symbol, x.Timeframe, x.OpenTime });
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_candle_ranges_ExchangeType_Symbol_Timeframe",
                table: "market_candle_ranges",
                columns: new[] { "ExchangeType", "Symbol", "Timeframe" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_candle_ranges");

            migrationBuilder.DropTable(
                name: "market_candles");
        }
    }
}
