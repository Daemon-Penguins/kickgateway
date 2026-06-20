using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipsPlayerSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClipsLeadInCount",
                table: "Broadcasters",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "ClipsShuffle",
                table: "Broadcasters",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ClipsSortMode",
                table: "Broadcasters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ClipsTimeWindow",
                table: "Broadcasters",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClipsLeadInCount",
                table: "Broadcasters");

            migrationBuilder.DropColumn(
                name: "ClipsShuffle",
                table: "Broadcasters");

            migrationBuilder.DropColumn(
                name: "ClipsSortMode",
                table: "Broadcasters");

            migrationBuilder.DropColumn(
                name: "ClipsTimeWindow",
                table: "Broadcasters");
        }
    }
}
