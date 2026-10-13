using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

/// <summary>Immutable evidence used to explain a Sonar MQR-compatible rating.</summary>
public sealed record MqrRatingData(
    int FindingCount = 0,
    string? WorstSeverity = null,
    long? RemediationSeconds = null,
    long? CodeLines = null,
    double? DebtRatioPercent = null,
    int? ReviewedHotspots = null,
    int? TotalHotspots = null,
    double? ReviewedPercent = null
);

/// <summary>A rating and its measurement state. Null rating means Not available.</summary>
public sealed record MqrRating(
    string? Rating,
    string State,
    MqrRatingData Data,
    string FormulaVersion,
    string? Reason = null
)
{
    public int? Rank =>
        Rating switch
        {
            "A" => 1,
            "B" => 2,
            "C" => 3,
            "D" => 4,
            "E" => 5,
            _ => null,
        };
}

public sealed record MqrScopeRatings(
    MqrRating Security,
    MqrRating Reliability,
    MqrRating Maintainability,
    MqrRating SecurityReview
);

public sealed record MqrRatingsSnapshot(
    string FormulaVersion,
    MqrScopeRatings Overall,
    MqrScopeRatings NewCode
);

/// <summary>Central MQR rating calculation over normalized, profile-classified evidence.</summary>
public static class MqrRatings
{
    public const string FormulaVersion = "sonar-mqr/v1";

    public static MqrRatingsSnapshot Calculate(
        ProfileDefinition profile,
        RuleCatalogIndex ruleCatalog,
        IngestionManifest manifest,
        IReadOnlyCollection<FindingOccurrence> findings,
        CodeQualityTotals overallQuality,
        CodeQualityTotals newCodeQuality,
        SecurityReviewResult overallReview,
        SecurityReviewResult newCodeReview,
        string? newCodeUnavailableReason
    )
    {
        var overall = Scope(false, overallQuality, overallReview);
        var newCode = Scope(true, newCodeQuality, newCodeReview);
        return new(FormulaVersion, overall, newCode);

        MqrScopeRatings Scope(
            bool newCodeScope,
            CodeQualityTotals quality,
            SecurityReviewResult review
        )
        {
            if (newCodeScope && newCodeUnavailableReason is not null)
            {
                var unavailable = NotMeasured(newCodeUnavailableReason);
                return new(unavailable, unavailable, unavailable, unavailable);
            }
            return new(
                WorstSeverity("vulnerability", newCodeScope),
                WorstSeverity("bug", newCodeScope),
                Maintainability(newCodeScope, quality),
                AnalyzerState("security_hotspot") ?? SecurityReview(review)
            );
        }

        MqrRating WorstSeverity(string category, bool newCodeScope)
        {
            if (AnalyzerState(category) is { } unavailable)
                return unavailable;
            var scoped = Findings(category, newCodeScope).ToArray();
            if (newCodeScope && scoped.Any(f => f.ComparisonState == "indeterminate"))
                return NotMeasured("finding_identity_indeterminate", scoped.Length);
            if (scoped.Any(f => f.Severity == "unknown"))
                return NotMeasured("severity_unclassified", scoped.Length);
            var worst = scoped
                .Select(f => AnalysisFindings.SeverityBucket(f.Severity))
                .OrderByDescending(SeverityRank)
                .FirstOrDefault();
            var rating = worst switch
            {
                "blocker" => "E",
                "high" => "D",
                "medium" => "C",
                "low" => "B",
                _ => "A",
            };
            return new(rating, "measured", new(scoped.Length, worst ?? "info"), FormulaVersion);
        }

        MqrRating Maintainability(bool newCodeScope, CodeQualityTotals quality)
        {
            if (AnalyzerState("code_smell") is { } unavailable)
                return unavailable;
            var scoped = Findings("code_smell", newCodeScope).ToArray();
            if (newCodeScope && scoped.Any(f => f.ComparisonState == "indeterminate"))
                return NotMeasured("finding_identity_indeterminate", scoped.Length);
            var codeLines =
                newCodeScope ? quality.NewLines
                : quality.CodeLines is { } lines ? checked((long)lines)
                : null;
            if (codeLines is null)
                return NotMeasured(
                    newCodeScope ? "new_code_lines_not_measured" : "code_lines_not_measured",
                    scoped.Length
                );
            if (codeLines == 0)
                return new(
                    null,
                    "not_applicable",
                    new(scoped.Length, CodeLines: 0),
                    FormulaVersion,
                    "no_code_lines_in_scope"
                );

            long debt = 0;
            foreach (var finding in scoped)
            {
                if (
                    ruleCatalog
                        .Resolve(finding.Tool, finding.RuleId)
                        ?.Remediation
                    is not { } remediation
                )
                    return new(
                        null,
                        "not_measured",
                        new(scoped.Length, RemediationSeconds: debt, CodeLines: codeLines),
                        FormulaVersion,
                        "partial_remediation_model"
                    );
                debt = checked(debt + remediation.DurationSeconds);
            }
            var ratio = 100d * debt / (codeLines.Value * 30d * 60d);
            var rating =
                ratio <= 5 ? "A"
                : ratio < 10 ? "B"
                : ratio < 20 ? "C"
                : ratio < 50 ? "D"
                : "E";
            return new(
                rating,
                "measured",
                new(
                    scoped.Length,
                    RemediationSeconds: debt,
                    CodeLines: codeLines,
                    DebtRatioPercent: ratio
                ),
                FormulaVersion
            );
        }

        MqrRating? AnalyzerState(string category)
        {
            if (ruleCatalog.HasAmbiguities)
                return NotMeasured("ambiguous_rule_catalogue");
            var expected = ruleCatalog
                .RulesInCategory(category)
                .Select(rule => rule.Tool)
                .Distinct()
                .ToArray();
            if (expected.Length == 0)
                return new(null, "not_applicable", new(), FormulaVersion, "no_applicable_rules");
            var reports = expected
                .SelectMany(tool =>
                    manifest.Reports.Where(report =>
                        report.Format == "sarif" && report.Tool == tool
                    )
                )
                .ToArray();
            if (reports.Any(report => report.State == "failed"))
                return new(null, "failed", new(), FormulaVersion, "analyzer_failed");
            if (
                expected.Any(tool =>
                    !reports.Any(report => report.Tool == tool && report.State == "measured")
                )
            )
                return NotMeasured("analyzer_not_executed");
            var expectedSet = expected.ToHashSet();
            if (
                findings.Any(f =>
                    f.Category == "unknown"
                    && manifest.Reports.Any(report =>
                        report.ReportId == f.ReportId && expectedSet.Contains(report.Tool)
                    )
                )
            )
                return NotMeasured("unclassified_rules");
            return null;
        }

        IEnumerable<FindingOccurrence> Findings(string category, bool newCodeScope) =>
            findings.Where(f =>
                !f.Excepted
                && f.Category == category
                && (!newCodeScope || f.ComparisonState is "new" or "indeterminate")
            );
    }

    private static MqrRating SecurityReview(SecurityReviewResult review)
    {
        if (review.State == "not_applicable" && review.Denominator == 0)
            return new(
                "A",
                "measured",
                new(ReviewedHotspots: 0, TotalHotspots: 0, ReviewedPercent: 100),
                FormulaVersion,
                "no_hotspots_to_review"
            );
        if (review.State != "measured" || review.Value is null)
            return new(
                null,
                review.State == "failed" ? "failed" : "not_measured",
                new(TotalHotspots: review.Denominator),
                FormulaVersion,
                review.Reason ?? "hotspots_not_measured"
            );
        var rating =
            review.Value >= 80 ? "A"
            : review.Value >= 70 ? "B"
            : review.Value >= 50 ? "C"
            : review.Value >= 30 ? "D"
            : "E";
        return new(
            rating,
            "measured",
            new(
                ReviewedHotspots: review.Numerator,
                TotalHotspots: review.Denominator,
                ReviewedPercent: review.Value
            ),
            FormulaVersion
        );
    }

    private static MqrRating NotMeasured(string reason, int findings = 0) =>
        new(null, "not_measured", new(findings), FormulaVersion, reason);

    private static int SeverityRank(string severity) =>
        severity switch
        {
            "blocker" => 5,
            "high" => 4,
            "medium" => 3,
            "low" => 2,
            _ => 1,
        };
}
