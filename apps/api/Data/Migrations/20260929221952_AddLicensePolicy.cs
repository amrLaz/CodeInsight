using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicensePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "license_policy_snapshot",
                table: "ingestions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "license_policies",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    allowed_licenses = table.Column<string>(type: "text", nullable: false),
                    denied_licenses = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_policies", x => x.scope_key);
                });

            migrationBuilder.InsertData(
                table: "license_policies",
                columns: new[] { "scope_key", "allowed_licenses", "denied_licenses", "updated_at", "updated_by", "version" },
                values: new object[] { "global", "[\"MIT\",\"Apache-2.0\",\"BSD-2-Clause\",\"BSD-3-Clause\",\"ISC\",\"0BSD\",\"Zlib\",\"BSL-1.0\",\"Unlicense\",\"CC0-1.0\",\"PostgreSQL\",\"Python-2.0\",\"LGPL-2.1-only\",\"LGPL-2.1-or-later\",\"LGPL-3.0-only\",\"LGPL-3.0-or-later\",\"LGPL-2.1\",\"LGPL-2.1\\u002B\",\"LGPL-3.0\",\"LGPL-3.0\\u002B\",\"MPL-2.0\",\"EPL-2.0\"]", "[\"GPL-2.0-only\",\"GPL-2.0-or-later\",\"GPL-3.0-only\",\"GPL-3.0-or-later\",\"AGPL-3.0-only\",\"AGPL-3.0-or-later\",\"GPL-2.0\",\"GPL-2.0\\u002B\",\"GPL-3.0\",\"GPL-3.0\\u002B\",\"AGPL-3.0\",\"SSPL-1.0\",\"BUSL-1.1\",\"CC-BY-NC-4.0\"]", new DateTime(2026, 9, 29, 0, 0, 0, 0, DateTimeKind.Utc), "migration", 1 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_policies");

            migrationBuilder.DropColumn(
                name: "license_policy_snapshot",
                table: "ingestions");
        }
    }
}
