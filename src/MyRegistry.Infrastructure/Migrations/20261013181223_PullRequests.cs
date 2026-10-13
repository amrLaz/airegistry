using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyRegistry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PullRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PrId",
                table: "artifact_versions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrState",
                table: "artifact_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrUrl",
                table: "artifact_versions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrId",
                table: "artifact_versions");

            migrationBuilder.DropColumn(
                name: "PrState",
                table: "artifact_versions");

            migrationBuilder.DropColumn(
                name: "PrUrl",
                table: "artifact_versions");
        }
    }
}
