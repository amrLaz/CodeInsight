using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bot_identity_id",
                table: "azure_devops_org_config",
                type: "character varying(255)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "analysis_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "decoration_revision",
                table: "analysis_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "error_count",
                table: "analysis_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "failure_code",
                table: "analysis_records",
                type: "character varying(100)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "github_check_run_id",
                table: "analysis_records",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "info_count",
                table: "analysis_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "pr_ado_org_url",
                table: "analysis_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_source_sha",
                table: "analysis_records",
                type: "character varying(255)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_target_sha",
                table: "analysis_records",
                type: "character varying(255)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_run_id",
                table: "analysis_records",
                type: "character varying(255)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "quality_gate_summary",
                table: "analysis_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "repository_url",
                table: "analysis_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "result_hash",
                table: "analysis_records",
                type: "character varying(64)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "started_at",
                table: "analysis_records",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UnixEpoch);

            migrationBuilder.AddColumn<int>(
                name: "warning_count",
                table: "analysis_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "analysis_sources",
                columns: table => new
                {
                    source_id = table.Column<string>(type: "text", nullable: false),
                    analysis_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    blob_name = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_sources", x => x.source_id);
                    table.ForeignKey(
                        name: "FK_analysis_sources_analysis_records_analysis_id",
                        column: x => x.analysis_id,
                        principalTable: "analysis_records",
                        principalColumn: "analysis_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analysis_records_project_key_provider_run_id",
                table: "analysis_records",
                columns: new[] { "project_key", "provider_run_id" },
                unique: true,
                filter: "provider_run_id <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_sources_analysis_id_path",
                table: "analysis_sources",
                columns: new[] { "analysis_id", "path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_sources");

            migrationBuilder.DropIndex(
                name: "IX_analysis_records_project_key_provider_run_id",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "bot_identity_id",
                table: "azure_devops_org_config");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "decoration_revision",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "error_count",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "failure_code",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "github_check_run_id",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "info_count",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "pr_ado_org_url",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "pr_source_sha",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "pr_target_sha",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "provider_run_id",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "quality_gate_summary",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "repository_url",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "result_hash",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "started_at",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "warning_count",
                table: "analysis_records");
        }
    }
}
