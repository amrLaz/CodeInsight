using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/insights/portfolio", PortfolioAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Daily aggregates over the caller's authorized projects. Every sum carries the number of projects it
    /// covers: an unmeasured project is left out, never counted as zero, and no average hides a project.
    /// </summary>
    private static async Task<IResult> PortfolioAsync(HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        var actor = await auth.RequireAsync(context, "user", ct);
        var keys = await auth.AllowedProjectKeysAsync(actor, ct: ct);
        var text = context.Request.Query["days"].ToString();
        var span = text.Length == 0 ? 90 : int.TryParse(text, out var parsed) && parsed is >= 7 and <= 365 ? parsed : throw new ApiException(422, "days must be between 7 and 365");
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        return Results.Ok(await BuildAsync(db, keys, to.AddDays(1 - span), to, ct));
    }

    internal static async Task<object> BuildAsync(AppDbContext db, IReadOnlyCollection<string> keys, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = db.Set<PortfolioSnapshot>().AsNoTracking().Where(x => keys.Contains(x.ProjectKey) && x.Day >= from && x.Day <= to);

        var days = await rows.GroupBy(x => x.Day).OrderBy(g => g.Key).Select(g => new
        {
            day = g.Key,
            projects = g.Count(),
            security = new { a = g.Count(x => x.SecurityRating == "A"), b = g.Count(x => x.SecurityRating == "B"), c = g.Count(x => x.SecurityRating == "C"), d = g.Count(x => x.SecurityRating == "D"), e = g.Count(x => x.SecurityRating == "E"), not_rated = g.Count(x => x.SecurityRating == null) },
            reliability = new { a = g.Count(x => x.ReliabilityRating == "A"), b = g.Count(x => x.ReliabilityRating == "B"), c = g.Count(x => x.ReliabilityRating == "C"), d = g.Count(x => x.ReliabilityRating == "D"), e = g.Count(x => x.ReliabilityRating == "E"), not_rated = g.Count(x => x.ReliabilityRating == null) },
            maintainability = new { a = g.Count(x => x.MaintainabilityRating == "A"), b = g.Count(x => x.MaintainabilityRating == "B"), c = g.Count(x => x.MaintainabilityRating == "C"), d = g.Count(x => x.MaintainabilityRating == "D"), e = g.Count(x => x.MaintainabilityRating == "E"), not_rated = g.Count(x => x.MaintainabilityRating == null) },
            security_review = new { a = g.Count(x => x.SecurityReviewRating == "A"), b = g.Count(x => x.SecurityReviewRating == "B"), c = g.Count(x => x.SecurityReviewRating == "C"), d = g.Count(x => x.SecurityReviewRating == "D"), e = g.Count(x => x.SecurityReviewRating == "E"), not_rated = g.Count(x => x.SecurityReviewRating == null) },
            findings_projects = g.Count(x => x.BlockerFindings != null),
            blocker = g.Sum(x => x.BlockerFindings),
            high = g.Sum(x => x.HighFindings),
            medium = g.Sum(x => x.MediumFindings),
            low = g.Sum(x => x.LowFindings),
            info = g.Sum(x => x.InfoFindings),
            unknown = g.Sum(x => x.UnknownSeverityFindings),
            sla_projects = g.Count(x => x.SlaBreached != null),
            sla_breached = g.Sum(x => x.SlaBreached),
            sla_due_soon = g.Sum(x => x.SlaDueSoon),
            coverage_projects = g.Count(x => x.Coverage != null),
            duplication_projects = g.Count(x => x.Duplication != null),
        }).ToListAsync(ct);

        var bounds = rows.GroupBy(x => x.ProjectKey).Select(g => new { ProjectKey = g.Key, First = g.Min(x => x.Day), Last = g.Max(x => x.Day) });
        var latest = await (from row in rows join bound in bounds on new { row.ProjectKey, row.Day } equals new { bound.ProjectKey, Day = bound.Last } select row)
            .OrderBy(x => x.ProjectKey).ToListAsync(ct);
        var earliest = (await (from row in rows join bound in bounds on new { row.ProjectKey, row.Day } equals new { bound.ProjectKey, Day = bound.First } select row)
            .ToListAsync(ct)).ToDictionary(x => x.ProjectKey);
        var withoutMainBranch = await db.Set<Project>().CountAsync(x => keys.Contains(x.Key) && !x.Archived && x.MainBranch == null, ct);

        return new
        {
            from,
            to,
            projects_without_main_branch = withoutMainBranch,
            days = days.Select(d => new
            {
                d.day,
                d.projects,
                ratings = new { d.security, d.reliability, d.maintainability, d.security_review },
                // A sum over zero measured projects is unknown, not zero.
                findings = d.findings_projects == 0 ? null : new { projects = d.findings_projects, d.blocker, d.high, d.medium, d.low, d.info, d.unknown },
                sla = d.sla_projects == 0 ? null : new { projects = d.sla_projects, breached = d.sla_breached, due_soon = d.sla_due_soon },
                coverage_projects = d.coverage_projects,
                duplication_projects = d.duplication_projects,
            }),
            projects = latest.Select(x =>
            {
                var start = earliest.GetValueOrDefault(x.ProjectKey);
                return new
                {
                    project_key = x.ProjectKey,
                    x.Day,
                    x.AnalysisId,
                    x.Source,
                    x.SecurityRating,
                    x.ReliabilityRating,
                    x.MaintainabilityRating,
                    x.SecurityReviewRating,
                    x.Coverage,
                    coverage_start = start?.Coverage,
                    x.Duplication,
                    duplication_start = start?.Duplication,
                    start_day = start?.Day,
                    x.BlockerFindings,
                    x.HighFindings,
                    x.SlaBreached,
                };
            }),
        };
    }
}
