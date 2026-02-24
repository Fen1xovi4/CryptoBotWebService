using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTradePnlFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "trades",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PnlDollar",
                table: "trades",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commission",
                table: "trades");

            migrationBuilder.DropColumn(
                name: "PnlDollar",
                table: "trades");
        }
    }
}
