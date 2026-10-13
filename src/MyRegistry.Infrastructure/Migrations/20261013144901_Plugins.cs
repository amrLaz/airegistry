using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyRegistry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Plugins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_components",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExactVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_components", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plugin_drafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    ComponentsJson = table.Column<string>(type: "text", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_drafts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_components_PluginVersionId",
                table: "plugin_components",
                column: "PluginVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_drafts_OwnerId",
                table: "plugin_drafts",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_components");

            migrationBuilder.DropTable(
                name: "plugin_drafts");
        }
    }
}
