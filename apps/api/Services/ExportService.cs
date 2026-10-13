using System.Globalization;
using System.Text;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

public static class ExportService
{
    private static string CsvField(object? value)
    {
        var text = value switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }

    public static byte[] Csv(IEnumerable<AnalysisRecord> records)
    {
        var output = new StringBuilder();
        void Row(IEnumerable<object?> fields) =>
            output
                .Append(
                    string.Join(
                        ',',
                        fields.Select(x =>
                        {
                            return CsvField(x);
                        })
                    )
                )
                .Append("\r\n");
        Row([
            "analysis_id",
            "project_key",
            "branch",
            "commit_sha",
            "ci_provider",
            "quality_gate",
            "created_at",
            "security_score",
            "maintainability_score",
            "reliability_score",
            "security_rating",
            "reliability_rating",
            "maintainability_rating",
            "security_review_rating",
            "vulnerabilities",
            "security_hotspots",
            "bugs",
            "code_smells",
            "coverage",
            "duplication",
            "dependency_vulnerabilities",
            "gate_failures",
            "completeness",
            "profile_id",
            "profile_version",
            "formula_version",
            "gate_snapshot",
        ]);
        foreach (var r in records)
            Row([
                r.AnalysisId,
                r.ProjectKey,
                r.Branch,
                r.CommitSha,
                r.CiProvider,
                r.QualityGate,
                r.CreatedAt,
                r.SecurityScore,
                r.MaintainabilityScore,
                r.ReliabilityScore,
                r.SecurityRating,
                r.ReliabilityRating,
                r.MaintainabilityRating,
                r.SecurityReviewRating,
                r.Vulnerabilities,
                r.SecurityHotspots,
                r.Bugs,
                r.CodeSmells,
                r.Coverage,
                r.Duplication,
                r.DependencyVulnerabilities,
                string.Join("; ", GateEvaluation.Parse<List<string>>(r.GateFailures, [])),
                r.Completeness,
                r.ProfileId,
                r.ProfileVersion,
                r.FormulaVersion,
                r.GateSnapshot,
            ]);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public static byte[] FindingsCsv(IEnumerable<FindingOccurrence> findings, IngestionManifest? manifest)
    {
        var output = new StringBuilder("id,finding_id,tool,tool_version,rule_id,severity,original_severity,category,path,line,message,excepted\r\n");
        foreach (var finding in findings)
        {
            var version = manifest?.Reports.FirstOrDefault(report => report.ReportId == finding.ReportId)?.ToolVersion;
            output.AppendJoin(',', new object?[] { finding.Id, finding.FindingId, finding.Tool, version, finding.RuleId, finding.Severity,
                finding.OriginalSeverity, finding.Category, finding.Path, finding.Line, finding.Message, finding.Excepted }.Select(CsvField)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

}
