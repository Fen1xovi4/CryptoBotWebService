using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBotWeb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRbacAndInviteCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add new columns first (before dropping IsAdmin)
            migrationBuilder.AddColumn<short>(
                name: "Role",
                table: "users",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvitedByUserId",
                table: "users",
                type: "uuid",
                nullable: true);

            // Migrate data: IsAdmin=true → Role=2 (Admin)
            migrationBuilder.Sql("UPDATE users SET \"Role\" = 2 WHERE \"IsAdmin\" = true;");

            // Now safe to drop old column
            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "users");

            migrationBuilder.CreateTable(
                name: "invite_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssignedRole = table.Column<short>(type: "smallint", nullable: false),
                    MaxUses = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    UsedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invite_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invite_codes_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invite_code_usages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    InviteCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invite_code_usages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invite_code_usages_invite_codes_InviteCodeId",
                        column: x => x.InviteCodeId,
                        principalTable: "invite_codes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invite_code_usages_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_InvitedByUserId",
                table: "users",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_invite_code_usages_InviteCodeId_UserId",
                table: "invite_code_usages",
                columns: new[] { "InviteCodeId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_invite_code_usages_UserId",
                table: "invite_code_usages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_invite_codes_Code",
                table: "invite_codes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invite_codes_CreatedByUserId",
                table: "invite_codes",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_users_users_InvitedByUserId",
                table: "users",
                column: "InvitedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_users_InvitedByUserId",
                table: "users");

            migrationBuilder.DropTable(
                name: "invite_code_usages");

            migrationBuilder.DropTable(
                name: "invite_codes");

            migrationBuilder.DropIndex(
                name: "IX_users_InvitedByUserId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "InvitedByUserId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "users");

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
