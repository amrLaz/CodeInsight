using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portfolio_snapshots",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    analysis_id = table.Column<string>(type: "text", nullable: false),
                    security_rating = table.Column<string>(type: "text", nullable: true),
                    reliability_rating = table.Column<string>(type: "text", nullable: true),
                    maintainability_rating = table.Column<string>(type: "text", nullable: true),
                    security_review_rating = table.Column<string>(type: "text", nullable: true),
                    coverage = table.Column<int>(type: "integer", nullable: true),
                    duplication = table.Column<int>(type: "integer", nullable: true),
                    blocker_findings = table.Column<int>(type: "integer", nullable: true),
                    high_findings = table.Column<int>(type: "integer", nullable: true),
                    medium_findings = table.Column<int>(type: "integer", nullable: true),
                    low_findings = table.Column<int>(type: "integer", nullable: true),
                    info_findings = table.Column<int>(type: "integer", nullable: true),
                    unknown_severity_findings = table.Column<int>(type: "integer", nullable: true),
                    sla_breached = table.Column<int>(type: "integer", nullable: true),
                    sla_due_soon = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_snapshots", x => new { x.project_key, x.day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_snapshots_day",
                table: "portfolio_snapshots",
                column: "day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portfolio_snapshots");
        }
    }
}
