using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNewCodeDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "main_branch",
                table: "projects",
                type: "character varying(500)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "new_code_snapshot",
                table: "ingestions",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "new_code_definitions",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: true),
                    branch = table.Column<string>(type: "text", nullable: true),
                    mode = table.Column<string>(type: "text", nullable: false),
                    number_of_days = table.Column<int>(type: "integer", nullable: true),
                    reference_branch = table.Column<string>(type: "text", nullable: true),
                    analysis_id = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_new_code_definitions", x => x.scope_key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_new_code_definitions_project_key_branch",
                table: "new_code_definitions",
                columns: new[] { "project_key", "branch" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "new_code_definitions");

            migrationBuilder.DropColumn(
                name: "main_branch",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "new_code_snapshot",
                table: "ingestions");
        }
    }
}
