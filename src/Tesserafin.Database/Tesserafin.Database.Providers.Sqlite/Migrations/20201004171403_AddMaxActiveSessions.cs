#pragma warning disable CS1591
#pragma warning disable SA1601

using Microsoft.EntityFrameworkCore.Migrations;

namespace Tesserafin.Server.Implementations.Migrations
{
    public partial class AddMaxActiveSessions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxActiveSessions",
                schema: "reefin",
                table: "Users",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxActiveSessions",
                schema: "reefin",
                table: "Users");
        }
    }
}
