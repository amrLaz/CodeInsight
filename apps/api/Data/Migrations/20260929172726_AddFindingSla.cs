using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFindingSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "on_sla_breach",
                table: "notification_channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "first_detected_at",
                table: "durable_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "severity",
                table: "durable_findings",
                type: "text",
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "sla_basis",
                table: "durable_findings",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "sla_due_at",
                table: "durable_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sla_notified_at",
                table: "durable_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sla_warn_at",
                table: "durable_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sla_policies",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: true),
                    blocker_days = table.Column<int>(type: "integer", nullable: true),
                    high_days = table.Column<int>(type: "integer", nullable: true),
                    medium_days = table.Column<int>(type: "integer", nullable: true),
                    low_days = table.Column<int>(type: "integer", nullable: true),
                    info_days = table.Column<int>(type: "integer", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_policies", x => x.scope_key);
                });

            migrationBuilder.InsertData(
                table: "sla_policies",
                columns: new[] { "scope_key", "blocker_days", "high_days", "info_days", "low_days", "medium_days", "project_key", "updated_at", "updated_by", "version" },
                values: new object[] { "global", 7, 30, null, 180, 90, null, new DateTime(2026, 9, 29, 0, 0, 0, 0, DateTimeKind.Utc), "migration", 1 });

            migrationBuilder.CreateIndex(
                name: "IX_durable_findings_sla_basis_sla_due_at",
                table: "durable_findings",
                columns: new[] { "sla_basis", "sla_due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sla_policies_project_key",
                table: "sla_policies",
                column: "project_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sla_policies");

            migrationBuilder.DropIndex(
                name: "IX_durable_findings_sla_basis_sla_due_at",
                table: "durable_findings");

            migrationBuilder.DropColumn(
                name: "on_sla_breach",
                table: "notification_channels");

            migrationBuilder.DropColumn(
                name: "first_detected_at",
                table: "durable_findings");

            migrationBuilder.DropColumn(
                name: "severity",
                table: "durable_findings");

            migrationBuilder.DropColumn(
                name: "sla_basis",
                table: "durable_findings");

            migrationBuilder.DropColumn(
                name: "sla_due_at",
                table: "durable_findings");

            migrationBuilder.DropColumn(
                name: "sla_notified_at",
                table: "durable_findings");

            migrationBuilder.DropColumn(
                name: "sla_warn_at",
                table: "durable_findings");
        }
    }
}
