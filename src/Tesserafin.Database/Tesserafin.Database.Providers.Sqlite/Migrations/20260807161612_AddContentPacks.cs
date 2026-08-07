using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tesserafin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    public partial class AddContentPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContentPackBrowsingPreference",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ContentPacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentPacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContentPackMemberships",
                columns: table => new
                {
                    PackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provenance = table.Column<int>(type: "INTEGER", nullable: false),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentPackMemberships", x => new { x.PackId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_ContentPackMemberships_BaseItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "BaseItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentPackMemberships_ContentPacks_PackId",
                        column: x => x.PackId,
                        principalTable: "ContentPacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentPackMemberships_ItemId",
                table: "ContentPackMemberships",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentPacks_NormalizedName",
                table: "ContentPacks",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentPacks_SortOrder",
                table: "ContentPacks",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentPackMemberships");

            migrationBuilder.DropTable(
                name: "ContentPacks");

            migrationBuilder.DropColumn(
                name: "ContentPackBrowsingPreference",
                table: "Users");
        }
    }
}
