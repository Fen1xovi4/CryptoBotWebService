using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create payment_wallets table
            migrationBuilder.CreateTable(
                name: "payment_wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AddressTrc20 = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddressBep20 = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_wallets", x => x.Id);
                });

            // 2. Create payment_sessions table (with guest support fields from the start)
            migrationBuilder.CreateTable(
                name: "payment_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plan = table.Column<short>(type: "smallint", nullable: false),
                    Network = table.Column<short>(type: "smallint", nullable: false),
                    Token = table.Column<short>(type: "smallint", nullable: false),
                    ExpectedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    TxHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReceivedAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuestToken = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedInviteCodeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_payment_sessions_payment_wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "payment_wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_sessions_users_ConfirmedByAdminId",
                        column: x => x.ConfirmedByAdminId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_payment_sessions_invite_codes_AssignedInviteCodeId",
                        column: x => x.AssignedInviteCodeId,
                        principalTable: "invite_codes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // 3. Indexes
            migrationBuilder.CreateIndex(
                name: "IX_payment_sessions_WalletId_Status",
                table: "payment_sessions",
                columns: new[] { "WalletId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_sessions_UserId_CreatedAt",
                table: "payment_sessions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_sessions_ConfirmedByAdminId",
                table: "payment_sessions",
                column: "ConfirmedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_sessions_AssignedInviteCodeId",
                table: "payment_sessions",
                column: "AssignedInviteCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_sessions_GuestToken",
                table: "payment_sessions",
                column: "GuestToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "payment_sessions");
            migrationBuilder.DropTable(name: "payment_wallets");
        }
    }
}
