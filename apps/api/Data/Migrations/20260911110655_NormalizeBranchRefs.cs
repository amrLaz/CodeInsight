using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <summary>
    /// Brings rows written before <see cref="Services.BranchRefs"/> to the stored form: the full
    /// ref. Providers report <c>refs/heads/x</c>, so a database fed by the CI is already canonical
    /// and every statement here is a no-op; only rows published with a bare branch name move.
    /// </summary>
    public partial class NormalizeBranchRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE analysis_records SET branch = 'refs/heads/' || branch
                WHERE branch <> '' AND branch NOT LIKE 'refs/%';
                """
            );

            // Branch of a durable finding is a filter column; its identity hash is left untouched,
            // so triage decisions stay attached to the findings they were taken on.
            migrationBuilder.Sql(
                """
                UPDATE durable_findings SET branch = 'refs/heads/' || branch
                WHERE branch <> '' AND branch NOT LIKE 'refs/%';
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE finding_identity_matches SET branch = 'refs/heads/' || branch
                WHERE branch <> '' AND branch NOT LIKE 'refs/%';
                """
            );

            // Joined against analysis_records.branch by the project branch metrics.
            migrationBuilder.Sql(
                """
                UPDATE branches SET name = 'refs/heads/' || name
                WHERE name <> '' AND name NOT LIKE 'refs/%';
                """
            );

            // (project_key, branch) is a primary key: a project holding both spellings would
            // collide. The archived flag is merged into the canonical row before the duplicate
            // goes, so an archived branch is never silently un-archived.
            migrationBuilder.Sql(
                """
                UPDATE archived_code_branches AS c SET archived = true
                FROM archived_code_branches AS s
                WHERE s.branch <> '' AND s.branch NOT LIKE 'refs/%' AND s.archived
                  AND c.project_key = s.project_key AND c.branch = 'refs/heads/' || s.branch;

                DELETE FROM archived_code_branches AS s
                WHERE s.branch <> '' AND s.branch NOT LIKE 'refs/%'
                  AND EXISTS (
                    SELECT 1 FROM archived_code_branches AS c
                    WHERE c.project_key = s.project_key AND c.branch = 'refs/heads/' || s.branch
                  );

                UPDATE archived_code_branches SET branch = 'refs/heads/' || branch
                WHERE branch <> '' AND branch NOT LIKE 'refs/%';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: which rows carried a short name is not recorded, and stripping
            // the prefix from every ref would invent a downgrade that never existed. Reads accept
            // both spellings, so the previous code runs unchanged against normalized rows.
        }
    }
}
