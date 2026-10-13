using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class RuleEndpoints
{
    private static readonly Dictionary<string, string> Languages = new()
    {
        ["py"] = "Python",
        ["js"] = "JavaScript",
        ["ts"] = "TypeScript",
        ["tsx"] = "TypeScript",
        ["jsx"] = "JavaScript",
        ["php"] = "PHP",
        ["cs"] = "C#",
        ["css"] = "CSS",
        ["scss"] = "CSS",
        ["c"] = "C",
        ["cpp"] = "C++",
        ["h"] = "C/C++",
        ["kt"] = "Kotlin",
        ["dart"] = "Dart",
        ["go"] = "Go",
        ["java"] = "Java",
        ["rb"] = "Ruby",
        ["rs"] = "Rust",
        ["yml"] = "YAML",
        ["yaml"] = "YAML",
        ["json"] = "JSON",
        ["xml"] = "XML",
        ["sh"] = "Shell",
    };

    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        app = app.MapGroup("").RequireAuthorization();
        app.MapGet("/api/v1/rules", ListAsync);
        // ASP.NET catch-all parameters must be last; inspect the literal suffix to preserve slash-containing rule keys.
        app.MapMethods(
            "/api/v1/rules/{**rule_path}",
            ["POST", "DELETE"],
            async (
                string rule_path,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                AuditService audit,
                CancellationToken ct
            ) =>
            {
                if (!rule_path.EndsWith("/suppress", StringComparison.Ordinal))
                    return Results.NotFound();
                var key = Uri.UnescapeDataString(rule_path[..^9]);
                var user = await auth.RequireAsync(context, "admin", ct);
                var note = context.Request.Query["note"].FirstOrDefault();
                if (context.Request.Method == "POST")
                {
                    if (!await db.Set<RuleSuppression>().AnyAsync(x => x.RuleKey == key, ct))
                    {
                        db.Add(
                            new RuleSuppression
                            {
                                Id = Guid.NewGuid().ToString(),
                                RuleKey = key,
                                SuppressedAt = DateTime.UtcNow,
                                Note = note,
                            }
                        );
                        audit.Log(
                            user.Email,
                            user.Role,
                            "rule.suppress",
                            "rule",
                            resourceLabel: key,
                            details: string.IsNullOrEmpty(note) ? null : new { note }
                        );
                        await db.SaveChangesAsync(ct);
                    }
                    return Results.Ok(new { status = "suppressed", rule_key = key });
                }
                await db.Set<RuleSuppression>().Where(x => x.RuleKey == key).ExecuteDeleteAsync(ct);
                audit.Log(user.Email, user.Role, "rule.unsuppress", "rule", resourceLabel: key);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { status = "active", rule_key = key });
            }
        );
        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var user = await auth.RequireAsync(context, ct: ct);
        var visible = await auth.AllowedProjectKeysAsync(user, ct: ct);
        var key = context.Request.Query["project_key"].ToString();
        if (key.Length > 0)
            await auth.AssertProjectAccessAsync(user, key, ct: ct);
        var query = db.Set<AnalysisRecord>()
            .AsNoTracking()
            .Where(x => key.Length > 0 ? x.ProjectKey == key : visible.Contains(x.ProjectKey));
        var rows =
            key.Length > 0
                ? await query
                    .OrderByDescending(x => x.CreatedAt)
                    .Take(10)
                    .Select(x => new { x.ProjectKey, x.AffectedFiles })
                    .ToListAsync(ct)
                : await query
                    .GroupBy(x => x.ProjectKey)
                    .Select(g =>
                        g.OrderByDescending(x => x.CreatedAt)
                            .Select(x => new { x.ProjectKey, x.AffectedFiles })
                            .First()
                    )
                    .ToListAsync(ct);
        var suppressions = (
            await db.Set<RuleSuppression>().Select(x => x.RuleKey).ToListAsync(ct)
        ).ToHashSet();
        var rules = new Dictionary<string, RuleSummary>();
        foreach (var row in rows)
            foreach (var (path, issue) in AnalysisFindings.Enumerate(row.AffectedFiles))
            {
                var rule = AnalysisFindings.Text(issue, "rule");
                if (rule.Length == 0 || AnalysisFindings.ScaPrefixes.Any(rule.StartsWith))
                    continue;
                if (!rules.TryGetValue(rule, out var summary))
                {
                    var rest = rule.Contains('/') ? rule[(rule.IndexOf('/') + 1)..] : rule;
                    var title = rule.Contains('/')
                        ? rest.Split('.').Last().Replace('-', ' ').Replace('_', ' ').Trim()
                        : rule;
                    if (title.Length == 0)
                        title = rest;
                    var severity = AnalysisFindings
                        .Text(issue, "severity", "info")
                        .ToUpperInvariant() switch
                    {
                        "BLOCKER" or "CRITICAL" => "Critical",
                        "HIGH" => "High",
                        "MEDIUM" or "WARNING" => "Medium",
                        _ => "Low",
                    };
                    var category = AnalysisFindings.Text(issue, "category", "vulnerability") switch
                    {
                        "bug" => "Reliability",
                        "code_smell" => "Maintainability",
                        _ => "Security",
                    };
                    var language = rule.Split('/')[0] switch
                    {
                        "bandit" => "Python",
                        "eslint" => "JavaScript",
                        "dart-analyze" => "Dart",
                        "phpstan" => "PHP",
                        "detect-secrets" or "gitleaks" => "Any",
                        "cppcheck" => "C/C++",
                        "detekt" => "Kotlin",
                        _ => Languages.GetValueOrDefault(
                            Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                            "Multi"
                        ),
                    };
                    summary = new RuleSummary(rule, title, category, severity, language);
                    rules[rule] = summary;
                }
                summary.Occurrences++;
                summary.Files.Add(path);
                summary.Projects.Add(row.ProjectKey);
            }
        // ponytail: query afresh so suppression changes are immediately consistent; add bounded authorization-scoped caching only if measured load requires it.
        return Results.Ok(
            rules
                .Values.OrderByDescending(x => x.Occurrences)
                .Select(x => new
                {
                    rule_id = x.Id,
                    title = x.Title,
                    category = x.Category,
                    severity = x.Severity,
                    language = x.Language,
                    occurrences = x.Occurrences,
                    file_count = x.Files.Count,
                    project_count = x.Projects.Count,
                    projects = x.Projects,
                    status = suppressions.Contains(x.Id) ? "suppressed" : "active",
                })
        );
    }

    private sealed class RuleSummary(
        string id,
        string title,
        string category,
        string severity,
        string language
    )
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Category { get; } = category;
        public string Severity { get; } = severity;
        public string Language { get; } = language;
        public int Occurrences { get; set; }
        public HashSet<string> Files { get; } = [];
        public HashSet<string> Projects { get; } = [];
    }
}

