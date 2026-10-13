using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

public static class AnalysisOccurrences
{
    public static IEnumerable<FindingOccurrence> From(AnalysisRecord analysis) =>
        AnalysisFindings
            .Enumerate(analysis.AffectedFiles)
            .Select(item => new FindingOccurrence
            {
                AnalysisId = analysis.AnalysisId,
                IngestionId = "",
                ReportId = "",
                Tool = AnalysisFindings.Text(item.Issue, "tool"),
                RuleId = AnalysisFindings.Text(
                    item.Issue,
                    "rule_id",
                    AnalysisFindings.Text(item.Issue, "rule")
                ),
                Severity = AnalysisFindings.Text(item.Issue, "severity", "unknown"),
                OriginalSeverity = AnalysisFindings.Text(item.Issue, "severity", "unknown"),
                Category = AnalysisFindings.Text(item.Issue, "category", "unknown"),
                Message = AnalysisFindings.Text(item.Issue, "message"),
                Path = item.Path,
                Line = AnalysisFindings.Line(item.Issue, "line"),
                Fingerprint = null,
            });
}
