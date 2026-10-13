using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPullRequestTargetBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ingestion used to clear the target branch of pull request analyses; the latest completed
            // manifest of each analysis still holds the canonical target recorded at ingestion.
            migrationBuilder.Sql(
                """
                UPDATE analysis_records AS a
                SET pr_target_branch = m.target
                FROM (
                    SELECT DISTINCT ON (analysis_id) analysis_id, manifest::jsonb ->> 'pr_target_branch' AS target
                    FROM ingestions
                    WHERE completed_at IS NOT NULL
                        AND analysis_id IN (
                            SELECT analysis_id FROM analysis_records
                            WHERE COALESCE(pr_id, '') <> '' AND COALESCE(pr_target_branch, '') = ''
                        )
                    ORDER BY analysis_id, revision DESC
                ) AS m
                WHERE a.analysis_id = m.analysis_id
                    AND COALESCE(a.pr_id, '') <> ''
                    AND COALESCE(a.pr_target_branch, '') = ''
                    AND COALESCE(m.target, '') <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The backfilled value is the recorded target: nothing to restore.
        }
    }
}
