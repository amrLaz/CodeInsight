using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMqrRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "maintainability_rating",
                table: "analysis_records",
                type: "character varying(1)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reliability_rating",
                table: "analysis_records",
                type: "character varying(1)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "security_rating",
                table: "analysis_records",
                type: "character varying(1)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "security_review_rating",
                table: "analysis_records",
                type: "character varying(1)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "maintainability_rating",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "reliability_rating",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "security_rating",
                table: "analysis_records");

            migrationBuilder.DropColumn(
                name: "security_review_rating",
                table: "analysis_records");
        }
    }
}
