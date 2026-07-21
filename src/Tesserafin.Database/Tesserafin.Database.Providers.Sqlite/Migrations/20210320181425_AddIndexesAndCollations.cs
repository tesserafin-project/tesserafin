#pragma warning disable CS1591
#pragma warning disable SA1601

using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Tesserafin.Server.Implementations.Migrations
{
    public partial class AddIndexesAndCollations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImageInfos_Users_UserId",
                schema: "reefin",
                table: "ImageInfos");

            migrationBuilder.DropForeignKey(
                name: "FK_Permissions_Users_Permission_Permissions_Guid",
                schema: "reefin",
                table: "Permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_Preferences_Users_Preference_Preferences_Guid",
                schema: "reefin",
                table: "Preferences");

            migrationBuilder.DropIndex(
                name: "IX_Preferences_Preference_Preferences_Guid",
                schema: "reefin",
                table: "Preferences");

            migrationBuilder.DropIndex(
                name: "IX_Permissions_Permission_Permissions_Guid",
                schema: "reefin",
                table: "Permissions");

            migrationBuilder.DropIndex(
                name: "IX_DisplayPreferences_UserId",
                schema: "reefin",
                table: "DisplayPreferences");

            migrationBuilder.DropIndex(
                name: "IX_CustomItemDisplayPreferences_UserId",
                schema: "reefin",
                table: "CustomItemDisplayPreferences");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                schema: "reefin",
                table: "Users",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "reefin",
                table: "Preferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "reefin",
                table: "Permissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE Preferences SET UserId = Preference_Preferences_Guid");
            migrationBuilder.Sql("UPDATE Permissions SET UserId = Permission_Permissions_Guid");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                schema: "reefin",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Preferences_UserId_Kind",
                schema: "reefin",
                table: "Preferences",
                columns: new[] { "UserId", "Kind" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_UserId_Kind",
                schema: "reefin",
                table: "Permissions",
                columns: new[] { "UserId", "Kind" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ImageInfos_Users_UserId",
                schema: "reefin",
                table: "ImageInfos",
                column: "UserId",
                principalSchema: "reefin",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Permissions_Users_UserId",
                schema: "reefin",
                table: "Permissions",
                column: "UserId",
                principalSchema: "reefin",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Preferences_Users_UserId",
                schema: "reefin",
                table: "Preferences",
                column: "UserId",
                principalSchema: "reefin",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImageInfos_Users_UserId",
                schema: "reefin",
                table: "ImageInfos");

            migrationBuilder.DropForeignKey(
                name: "FK_Permissions_Users_UserId",
                schema: "reefin",
                table: "Permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_Preferences_Users_UserId",
                schema: "reefin",
                table: "Preferences");

            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                schema: "reefin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Preferences_UserId_Kind",
                schema: "reefin",
                table: "Preferences");

            migrationBuilder.DropIndex(
                name: "IX_Permissions_UserId_Kind",
                schema: "reefin",
                table: "Permissions");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "reefin",
                table: "Preferences");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "reefin",
                table: "Permissions");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                schema: "reefin",
                table: "Users",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 255,
                oldCollation: "NOCASE");

            migrationBuilder.CreateIndex(
                name: "IX_Preferences_Preference_Preferences_Guid",
                schema: "reefin",
                table: "Preferences",
                column: "Preference_Preferences_Guid");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Permission_Permissions_Guid",
                schema: "reefin",
                table: "Permissions",
                column: "Permission_Permissions_Guid");

            migrationBuilder.CreateIndex(
                name: "IX_DisplayPreferences_UserId",
                schema: "reefin",
                table: "DisplayPreferences",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomItemDisplayPreferences_UserId",
                schema: "reefin",
                table: "CustomItemDisplayPreferences",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ImageInfos_Users_UserId",
                schema: "reefin",
                table: "ImageInfos",
                column: "UserId",
                principalSchema: "reefin",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Permissions_Users_Permission_Permissions_Guid",
                schema: "reefin",
                table: "Permissions",
                column: "Permission_Permissions_Guid",
                principalSchema: "reefin",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Preferences_Users_Preference_Preferences_Guid",
                schema: "reefin",
                table: "Preferences",
                column: "Preference_Preferences_Guid",
                principalSchema: "reefin",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
