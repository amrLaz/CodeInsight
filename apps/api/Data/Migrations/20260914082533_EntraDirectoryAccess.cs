using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntraDirectoryAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_silent",
                table: "oidc_transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "entra_access_groups",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(64)", nullable: false),
                    object_id = table.Column<string>(type: "character varying(64)", nullable: false),
                    display_name = table.Column<string>(type: "character varying(255)", nullable: false),
                    role = table.Column<string>(type: "character varying(50)", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entra_access_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entra_group_grants",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    group_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(20)", nullable: false),
                    scope_id = table.Column<string>(type: "character varying(100)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entra_group_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_entra_group_grants_entra_access_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "entra_access_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entra_access_groups_role",
                table: "entra_access_groups",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "IX_entra_access_groups_tenant_id_object_id",
                table: "entra_access_groups",
                columns: new[] { "tenant_id", "object_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entra_group_grants_group_id",
                table: "entra_group_grants",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_entra_group_grants_group_id_scope_type_scope_id",
                table: "entra_group_grants",
                columns: new[] { "group_id", "scope_type", "scope_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entra_group_grants_scope_id",
                table: "entra_group_grants",
                column: "scope_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entra_group_grants");

            migrationBuilder.DropTable(
                name: "entra_access_groups");

            migrationBuilder.DropColumn(
                name: "is_silent",
                table: "oidc_transactions");
        }
    }
}
