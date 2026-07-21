using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tesserafin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEasyPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EasyPassword",
                schema: "tesserafin",
                table: "Users");

            migrationBuilder.RenameTable(
                name: "Users",
                schema: "tesserafin",
                newName: "Users");

            migrationBuilder.RenameTable(
                name: "Preferences",
                schema: "tesserafin",
                newName: "Preferences");

            migrationBuilder.RenameTable(
                name: "Permissions",
                schema: "tesserafin",
                newName: "Permissions");

            migrationBuilder.RenameTable(
                name: "ItemDisplayPreferences",
                schema: "tesserafin",
                newName: "ItemDisplayPreferences");

            migrationBuilder.RenameTable(
                name: "ImageInfos",
                schema: "tesserafin",
                newName: "ImageInfos");

            migrationBuilder.RenameTable(
                name: "HomeSection",
                schema: "tesserafin",
                newName: "HomeSection");

            migrationBuilder.RenameTable(
                name: "DisplayPreferences",
                schema: "tesserafin",
                newName: "DisplayPreferences");

            migrationBuilder.RenameTable(
                name: "Devices",
                schema: "tesserafin",
                newName: "Devices");

            migrationBuilder.RenameTable(
                name: "DeviceOptions",
                schema: "tesserafin",
                newName: "DeviceOptions");

            migrationBuilder.RenameTable(
                name: "CustomItemDisplayPreferences",
                schema: "tesserafin",
                newName: "CustomItemDisplayPreferences");

            migrationBuilder.RenameTable(
                name: "ApiKeys",
                schema: "tesserafin",
                newName: "ApiKeys");

            migrationBuilder.RenameTable(
                name: "ActivityLogs",
                schema: "tesserafin",
                newName: "ActivityLogs");

            migrationBuilder.RenameTable(
                name: "AccessSchedules",
                schema: "tesserafin",
                newName: "AccessSchedules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tesserafin");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "Users",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "Preferences",
                newName: "Preferences",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "Permissions",
                newName: "Permissions",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "ItemDisplayPreferences",
                newName: "ItemDisplayPreferences",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "ImageInfos",
                newName: "ImageInfos",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "HomeSection",
                newName: "HomeSection",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "DisplayPreferences",
                newName: "DisplayPreferences",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "Devices",
                newName: "Devices",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "DeviceOptions",
                newName: "DeviceOptions",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "CustomItemDisplayPreferences",
                newName: "CustomItemDisplayPreferences",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "ApiKeys",
                newName: "ApiKeys",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "ActivityLogs",
                newName: "ActivityLogs",
                newSchema: "tesserafin");

            migrationBuilder.RenameTable(
                name: "AccessSchedules",
                newName: "AccessSchedules",
                newSchema: "tesserafin");

            migrationBuilder.AddColumn<string>(
                name: "EasyPassword",
                schema: "tesserafin",
                table: "Users",
                type: "TEXT",
                maxLength: 65535,
                nullable: true);
        }
    }
}
