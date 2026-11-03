using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyRegistry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CommentReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comment_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comment_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_comment_reports_CommentId_UserId",
                table: "comment_reports",
                columns: new[] { "CommentId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comment_reports");
        }
    }
}
