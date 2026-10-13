using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyRegistry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GovernanceContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governance",
                columns: table => new
                {
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Risk = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DataClass = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReviewEveryDays = table.Column<int>(type: "integer", nullable: true),
                    LastReviewed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Capabilities = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance", x => x.ArtifactId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "governance");
        }
    }
}
