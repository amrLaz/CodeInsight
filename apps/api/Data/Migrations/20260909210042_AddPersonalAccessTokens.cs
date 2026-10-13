using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner_user_id",
                table: "platform_api_tokens",
                type: "character varying(36)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_tokens_owner_user_id",
                table: "platform_api_tokens",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_platform_api_tokens_owner_user_id",
                table: "platform_api_tokens");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                table: "platform_api_tokens");
        }
    }
}
