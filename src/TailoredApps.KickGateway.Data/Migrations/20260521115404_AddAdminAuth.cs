using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdminLoginClient",
                table: "ClientApps",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KickUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminUserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    KickClientAppId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminUserRoles_AdminUsers_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdminUserRoles_ClientApps_KickClientAppId",
                        column: x => x.KickClientAppId,
                        principalTable: "ClientApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AdminUsers",
                columns: new[] { "Id", "CreatedAt", "Email", "IsEnabled", "KickUserId", "LastLoginAt", "UpdatedAt", "Username" },
                values: new object[] { new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 5, 21, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "", null, new DateTime(2026, 5, 21, 0, 0, 0, 0, DateTimeKind.Utc), "superadmin" });

            migrationBuilder.InsertData(
                table: "AdminUserRoles",
                columns: new[] { "Id", "AdminUserId", "GrantedAt", "KickClientAppId", "Role" },
                values: new object[] { new Guid("22222222-2222-2222-2222-222222222222"), new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 5, 21, 0, 0, 0, 0, DateTimeKind.Utc), null, 1 });

            migrationBuilder.CreateIndex(
                name: "IX_ClientApps_IsAdminLoginClient",
                table: "ClientApps",
                column: "IsAdminLoginClient",
                unique: true,
                filter: "[IsAdminLoginClient] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserRoles_AdminUserId_KickClientAppId_Role",
                table: "AdminUserRoles",
                columns: new[] { "AdminUserId", "KickClientAppId", "Role" },
                unique: true,
                filter: "[KickClientAppId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserRoles_AdminUserId_Role",
                table: "AdminUserRoles",
                columns: new[] { "AdminUserId", "Role" },
                unique: true,
                filter: "[KickClientAppId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserRoles_KickClientAppId",
                table: "AdminUserRoles",
                column: "KickClientAppId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_KickUserId",
                table: "AdminUsers",
                column: "KickUserId",
                unique: true,
                filter: "[KickUserId] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Username",
                table: "AdminUsers",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminUserRoles");

            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropIndex(
                name: "IX_ClientApps_IsAdminLoginClient",
                table: "ClientApps");

            migrationBuilder.DropColumn(
                name: "IsAdminLoginClient",
                table: "ClientApps");
        }
    }
}
