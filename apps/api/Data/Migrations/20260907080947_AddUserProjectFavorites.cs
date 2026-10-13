using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProjectFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_project_favorites",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_project_favorites", x => new { x.user_id, x.project_id });
                    table.ForeignKey(
                        name: "FK_user_project_favorites_platform_users_user_id",
                        column: x => x.user_id,
                        principalTable: "platform_users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_project_favorites_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_project_favorites_project_id",
                table: "user_project_favorites",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_project_favorites");
        }
    }
}
