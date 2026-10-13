using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyRegistry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Feeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommitSha",
                table: "artifact_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FeedId",
                table: "artifact_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitPath",
                table: "artifact_versions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "feed_sync_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Commit = table.Column<string>(type: "text", nullable: true),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Deprecated = table.Column<int>(type: "integer", nullable: false),
                    Quarantined = table.Column<int>(type: "integer", nullable: false),
                    Messages = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feed_sync_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feeds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Branch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PatRef = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    NamespaceOverride = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCommit = table.Column<string>(type: "text", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feeds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_versions_FeedId",
                table: "artifact_versions",
                column: "FeedId");

            migrationBuilder.CreateIndex(
                name: "IX_feed_sync_reports_FeedId_StartedAt",
                table: "feed_sync_reports",
                columns: new[] { "FeedId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feed_sync_reports");

            migrationBuilder.DropTable(
                name: "feeds");

            migrationBuilder.DropIndex(
                name: "IX_artifact_versions_FeedId",
                table: "artifact_versions");

            migrationBuilder.DropColumn(
                name: "CommitSha",
                table: "artifact_versions");

            migrationBuilder.DropColumn(
                name: "FeedId",
                table: "artifact_versions");

            migrationBuilder.DropColumn(
                name: "GitPath",
                table: "artifact_versions");
        }
    }
}
