using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFindingWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "work_items_enabled",
                table: "azure_devops_org_config",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "finding_work_items",
                columns: table => new
                {
                    finding_id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    ado_project = table.Column<string>(type: "text", nullable: false),
                    work_item_id = table.Column<int>(type: "integer", nullable: true),
                    url = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finding_work_items", x => x.finding_id);
                });

            migrationBuilder.CreateTable(
                name: "work_item_settings",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    work_item_type = table.Column<string>(type: "text", nullable: false),
                    area_path = table.Column<string>(type: "text", nullable: true),
                    iteration_path = table.Column<string>(type: "text", nullable: true),
                    auto_min_severity = table.Column<string>(type: "text", nullable: true),
                    auto_on_sla_breach = table.Column<bool>(type: "boolean", nullable: false),
                    auto_max_per_run = table.Column<int>(type: "integer", nullable: false),
                    auto_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_settings", x => x.project_key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_finding_work_items_project_key_state",
                table: "finding_work_items",
                columns: new[] { "project_key", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finding_work_items");

            migrationBuilder.DropTable(
                name: "work_item_settings");

            migrationBuilder.DropColumn(
                name: "work_items_enabled",
                table: "azure_devops_org_config");
        }
    }
}
