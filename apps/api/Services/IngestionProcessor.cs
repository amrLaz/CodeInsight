using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Services;

public sealed class IngestionProcessor(
    AppDbContext db,
    ReportStorage storage,
    IOptions<IngestionOptions> options
)
{
    public async Task ProcessAsync(string id, string leaseOwner, CancellationToken ct)
    {
        var row = await db.Set<IngestionRecord>().SingleAsync(x => x.Id == id, ct);
        if (row.State == "completed")
            return;
        var manifest = JsonSerializer.Deserialize<IngestionManifest>(
            row.Manifest,
            ApiJson.Options
        )!;
        var profile = JsonSerializer.Deserialize<ProfileDefinition>(
            row.ProfileSnapshot,
            ApiJson.Options
        )!;
        var ruleCatalog = ExecutableProfiles.BuildRuleIndex(profile);
        var scope = CodeScopes.Read(row.CodeScopeSnapshot);
        var sourceDiff = manifest.GitDiff with
        {
            Files = manifest
                .GitDiff.Files.Where(f => CodeScopes.IsSource(scope.Definition, f.Path))
                .ToList(),
        };
        var codeData = new CodeQualitySnapshot();
        var reports = await db.Set<IngestionReport>()
            .Where(x => x.IngestionId == id)
            .ToListAsync(ct);
        var findings = new List<FindingOccurrence>();
        var components = new List<SbomComponent>();
        var coverage = new Dictionary<(string Path, int Line), bool>();
        var exceptions =
            JsonSerializer.Deserialize<List<ProfileException>>(
                row.ExceptionSnapshot,
                ApiJson.Options
            ) ?? [];
        var licensePolicy = LicensePolicies.Effective(row.LicensePolicySnapshot, profile);
        var allowedLicenses = licensePolicy.Allowed.ToHashSet(StringComparer.Ordinal);
        var deniedLicenses = licensePolicy.Denied.ToHashSet(StringComparer.Ordinal);
        var coverageMeasured = false;
        var assessedSboms = 0;
        long testsTotal = 0, testsFailed = 0, testsSkipped = 0, testDurationMilliseconds = 0;
        long evidenceBytes = 0;
        foreach (var descriptor in manifest.Reports.Where(x => x.State == "measured"))
        {
            var report = reports.Single(x => x.ReportId == descriptor.ReportId);
            if (report.State != "received")
                throw new InvalidDataException("Finalized report was not received");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ParseTimeoutSeconds));
            await using var stream = await storage.OpenAsync(report.BlobName, timeout.Token);
            ImportedReport imported;
            try
            {
                imported = await ReportImports.ParseAsync(
                    stream,
                    descriptor.Format,
                    descriptor.FormatVersion,
                    descriptor.Tool,
                    timeout.Token,
                    manifest.SourceRoot
                );
            }
            catch (InvalidDataException ex)
            {
                report.Error = ex.Message[..Math.Min(ex.Message.Length, 500)];
                await db.SaveChangesAsync(ct);
                throw;
            }
            coverageMeasured |= descriptor.Format is "cobertura" or "lcov";
            imported = imported with
            {
                CodeData = imported.CodeData with
                {
                    Branches = imported
                        .CodeData.Branches.Where(x => CodeScopes.IsSource(scope.Definition, x.Path))
                        .ToList(),
                    Metrics = imported
                        .CodeData.Metrics.Where(x => CodeScopes.IsSource(scope.Definition, x.Path))
                        .ToList(),
                    Duplicates = imported
                        .CodeData.Duplicates.Where(x =>
                            CodeScopes.IsSource(scope.Definition, x.First.Path)
                            && CodeScopes.IsSource(scope.Definition, x.Second.Path)
                        )
                        .ToList(),
                    SourceFiles = imported
                        .CodeData.SourceFiles.Where(x => CodeScopes.IsSource(scope.Definition, x))
                        .ToList(),
                },
            };
            CodeQualityData.AddReport(codeData, imported, descriptor);
            if (imported.VulnerabilityAssessmentMeasured == true)
                assessedSboms++;
            if (imported.Tests is { } tests)
            {
                testsTotal = checked(testsTotal + tests.Total);
                testsFailed = checked(testsFailed + tests.Failed);
                testsSkipped = checked(testsSkipped + tests.Skipped);
                testDurationMilliseconds = checked(testDurationMilliseconds + tests.DurationMilliseconds);
            }
            if (
                (long)findings.Count
                    + components.Count
                    + coverage.Count
                    + imported.Findings.Count
                    + imported.Components.Count
                    + imported.Coverage.Count
                > 250_000
            )
                throw new InvalidDataException(
                    "Publication exceeds the 250000 normalized-entry limit"
                );
            foreach (var f in imported.Findings)
            {
                if (!CodeScopes.Includes(scope.Definition, f.Path))
                    continue;
                var classification = ClassifyFinding(profile, ruleCatalog, descriptor, f);
                findings.Add(
                    new FindingOccurrence
                    {
                        IngestionId = id,
                        AnalysisId = row.AnalysisId,
                        ReportId = descriptor.ReportId,
                        Tool = descriptor.Tool,
                        OriginalTool = f.Tool,
                        RuleId = f.RuleId,
                        Severity = classification.Severity,
                        OriginalSeverity = f.OriginalSeverity,
                        Category = classification.Category,
                        Message = f.Message,
                        Path = f.Path,
                        Line = f.Line,
                        Fingerprint = f.Fingerprint,
                        Evidence = FindingEvidenceLinks.Serialize(f.Evidence, ref evidenceBytes),
                        Excepted = exceptions.Any(x =>
                            x.Tool == descriptor.Tool
                            && x.RuleId == f.RuleId
                            && (x.Path is null || x.Path == f.Path)
                        ),
                    }
                );
            }
            foreach (
                var c in imported.Coverage.Where(c => CodeScopes.IsSource(scope.Definition, c.Path))
            )
                coverage[(c.Path, c.Line)] =
                    coverage.GetValueOrDefault((c.Path, c.Line)) || c.Covered;
            foreach (var c in imported.Components)
            {
                var decision = LicensePolicies.Decide(c.Licenses, allowedLicenses, deniedLicenses);
                components.Add(
                    new SbomComponent
                    {
                        IngestionId = id,
                        AnalysisId = row.AnalysisId,
                        ReportId = descriptor.ReportId,
                        Reference = c.Reference,
                        Name = c.Name,
                        Version = c.Version,
                        Purl = c.Purl,
                        Ecosystem = c.Ecosystem,
                        Licenses = JsonSerializer.Serialize(c.Licenses, ApiJson.Options),
                        Relationship = c.Relationship,
                        LicenseDecision = decision,
                        Source = descriptor.Tool + "/" + descriptor.ToolVersion,
                        Vulnerabilities = JsonSerializer.Serialize(
                            imported.Vulnerabilities.Where(x =>
                                x.ComponentReference == c.Reference
                            ),
                            ApiJson.Options
                        ),
                        Dependencies = JsonSerializer.Serialize(
                            imported.Dependencies.Where(x => x.From == c.Reference),
                            ApiJson.Options
                        ),
                    }
                );
            }
        }
        var completeTools =
            profile.RequiredTools.All(t =>
                manifest.Reports.Any(r => r.Tool == t && r.State == "measured")
                && manifest.Reports.Where(r => r.Tool == t).All(r => r.State == "measured")
            )
            && profile.AnalyzerConfigurations.All(c =>
                manifest.Reports.Any(r =>
                    r.Tool == c.Tool && r.State == "measured"
                )
            );
        var configuration = ExecutableProfiles.Evaluate(profile, manifest);
        row.CodeData = CodeQualityData.Serialize(codeData);
        row.Comparison = await CompareAsync(row, manifest, findings, coverage, completeTools, ct);
        using var initialComparison = JsonDocument.Parse(row.Comparison);
        var overallSecurityReview = SecurityReviewEvaluation.Apply(
            profile.SecurityReview with { Scope = "global" },
            row.SecurityReviewSnapshot,
            manifest,
            row.CreatedAt,
            findings,
            FindingCount(profile, ruleCatalog, manifest, findings, completeTools, "security_hotspot")
                is not null,
            findings.Where(f => f.ComparisonState == "new").ToArray(),
            initialComparison.RootElement.GetProperty("state").GetString() == "available"
                && !findings.Any(f =>
                    f.Category == "security_hotspot" && f.ComparisonState == "indeterminate"
                )
        );
        var newCodeSecurityReview = SecurityReviewEvaluation.Apply(
            profile.SecurityReview with { Scope = "new_code" },
            row.SecurityReviewSnapshot,
            manifest,
            row.CreatedAt,
            findings,
            FindingCount(profile, ruleCatalog, manifest, findings, completeTools, "security_hotspot") is not null,
            findings.Where(f => f.ComparisonState == "new").ToArray(),
            initialComparison.RootElement.GetProperty("state").GetString() == "available"
                && !findings.Any(f => f.Category == "security_hotspot" && f.ComparisonState == "indeterminate")
        );
        var securityReview = profile.SecurityReview.Scope == "new_code" ? newCodeSecurityReview : overallSecurityReview;
        if (overallSecurityReview.AppliedExceptionDecisionIds.Length > 0 || newCodeSecurityReview.AppliedExceptionDecisionIds.Length > 0)
            row.Comparison = await CompareAsync(
                row,
                manifest,
                findings,
                coverage,
                completeTools,
                ct
            );
        var coverageComplete =
            coverageMeasured
            && manifest
                .Reports.Where(r => r.Format is "cobertura" or "lcov")
                .All(r => r.State == "measured");
        var coverageRows = coverage
            .Select(c => new ImportedCoverage(c.Key.Path, c.Key.Line, c.Value))
            .ToArray();
        CodeQualityTotals Quality(bool newCode)
        {
            var qualityDiff =
                newCode
                && initialComparison.RootElement.GetProperty("state").GetString() != "available"
                    ? sourceDiff with
                    {
                        State = "unavailable",
                    }
                    : sourceDiff;
            var value = CodeQualityData.Summarize(
                codeData,
                coverageRows,
                newCode: newCode,
                diff: qualityDiff,
                coverageMeasured: coverageComplete
            );
            if (manifest.Reports.Any(r => r.Format == "jscpd" && r.State != "measured"))
                value = value with { DuplicationState = "unavailable", DuplicateBlocks = null };
            if (manifest.Reports.Any(r => r.Format == "cloc" && r.State != "measured"))
                value = value with { CodeLines = null };
            var complexityReports = manifest.Reports.Any(r => r.Format == "lizard")
                ? manifest.Reports.Where(r => r.Format == "lizard")
                : manifest.Reports.Where(r => r.Format == "cobertura");
            if (complexityReports.Any(r => r.State != "measured"))
                value = value with { ClassComplexity = null };
            return value;
        }
        var globalQuality = Quality(false);
        var newQuality = Quality(true);
        var usesMqrRatings = profile.Conditions.Any(condition =>
            condition.Metric.EndsWith("_rating", StringComparison.Ordinal)
        );
        using var comparison = JsonDocument.Parse(row.Comparison);
        var comparisonState = comparison.RootElement.GetProperty("state").GetString();
        var newCodeUnavailableReason =
            comparisonState == "available"
                ? null
                : comparison.RootElement.TryGetProperty("reason", out var comparisonReason)
                    ? comparisonReason.GetString() ?? "comparison_unavailable"
                    : "comparison_unavailable";
        var ratings = usesMqrRatings
            ? MqrRatings.Calculate(
                profile,
                ruleCatalog,
                manifest,
                findings,
                globalQuality,
                newQuality,
                overallSecurityReview,
                newCodeSecurityReview,
                newCodeUnavailableReason
            )
            : null;
        var metrics = new Dictionary<string, double?>();
        foreach (
            var (metric, category) in new[]
            {
                ("bugs", "bug"),
                ("code_smells", "code_smell"),
                ("vulnerabilities", "vulnerability"),
                ("security_hotspots", "security_hotspot"),
            }
        )
            metrics[metric] = FindingCount(profile, ruleCatalog, manifest, findings, completeTools, category);
        metrics["coverage"] = usesMqrRatings
            ? CombinedCoverage(globalQuality).Percentage
            : coverageComplete && coverage.Count > 0
                ? 100.0 * coverage.Count(x => x.Value) / coverage.Count
                : null;
        if (ratings is not null)
        {
            metrics["security_rating"] = ratings.Overall.Security.Rank;
            metrics["reliability_rating"] = ratings.Overall.Reliability.Rank;
            metrics["maintainability_rating"] = ratings.Overall.Maintainability.Rank;
            metrics["security_review_rating"] = ratings.Overall.SecurityReview.Rank;
            metrics["duplicated_lines_density"] = globalQuality.DuplicatedLinesDensity.Percentage;
        }
        var sboms = manifest.Reports.Where(r => r.Format is "cyclonedx" or "spdx").ToArray();
        metrics["dependency_vulnerabilities"] =
            assessedSboms > 0 && assessedSboms == sboms.Length && completeTools
                ? components.Sum(c =>
                    JsonSerializer.Deserialize<JsonElement>(c.Vulnerabilities).GetArrayLength()
                )
                : null;
        metrics["license_violations"] =
            components.Count > 0
            && sboms.All(r => r.State == "measured")
            && components.All(c => c.LicenseDecision != "review")
                ? components.Count(c => c.LicenseDecision == "denied")
                : null;
        var testReports = manifest.Reports.Where(r => r.Format is "trx" or "junit").ToArray();
        if (testReports.Length > 0)
        {
            var testsMeasured = testReports.All(r => r.State == "measured");
            metrics["tests_total"] = testsMeasured ? testsTotal : null;
            metrics["tests_failed"] = testsMeasured ? testsFailed : null;
            metrics["tests_skipped"] = testsMeasured ? testsSkipped : null;
            metrics["test_duration_ms"] = testsMeasured ? testDurationMilliseconds : null;
        }
        foreach (
            var metric in new[]
            {
                "branch_coverage",
                "condition_coverage",
                "duplicate_blocks",
                "code_lines",
                "complexity",
            }
        )
            metrics[metric] = QualityMetric(globalQuality, metric).Value;
        var metricStates = metrics.ToDictionary(
            x => x.Key,
            x =>
            {
                var ratingState = x.Key switch
                {
                    "security_rating" => ratings?.Overall.Security.State,
                    "reliability_rating" => ratings?.Overall.Reliability.State,
                    "maintainability_rating" => ratings?.Overall.Maintainability.State,
                    "security_review_rating" => ratings?.Overall.SecurityReview.State,
                    _ => null,
                };
                if (ratingState is not null) return ratingState;
                var sources = manifest
                    .Reports.Where(r =>
                        x.Key switch
                        {
                            "coverage" or "branch_coverage" or "condition_coverage" => r.Format
                                is "cobertura"
                                    or "lcov",
                            "duplicate_blocks" or "duplicated_lines" => r.Format == "jscpd",
                            "duplicated_lines_density" => r.Format is "jscpd" or "cloc",
                            "code_lines" => r.Format == "cloc",
                            "complexity" => manifest.Reports.Any(report => report.Format == "lizard")
                                ? r.Format == "lizard"
                                : r.Format == "cobertura",
                            "dependency_vulnerabilities" or "license_violations" => r.Format
                                is "cyclonedx"
                                    or "spdx",
                            "tests_total" or "tests_failed" or "tests_skipped" or "test_duration_ms" => r.Format
                                is "trx"
                                    or "junit",
                            _ => r.Format == "sarif",
                        }
                    )
                    .ToArray();
                return sources.Any(r => r.State == "failed") ? "failed"
                    : sources.Any(r => r.State == "absent") ? "absent"
                    : x.Value is not null ? "measured"
                    : sources.Length == 0 && profile.RequiredMetrics.Contains(x.Key) ? "absent"
                    : "not_measured";
            }
        );
        var conditions = EvaluateConditions(
            profile,
            metrics,
            metricStates,
            comparison.RootElement,
            sourceDiff,
            coverage.Count(x => x.Value),
            coverage.Count,
            coverageComplete,
            globalQuality,
            newQuality
        ,
            ratings
        );
        if (profile.SecurityReview.MinimumHotspotReviewPercent is { } reviewThreshold)
            conditions =
            [
                .. conditions,
                new ConditionResult(
                    "hotspot_review",
                    ">=",
                    true,
                    reviewThreshold,
                    securityReview.Value,
                    securityReview.State,
                    securityReview.Result,
                    "security-review/frozen-decisions/v1",
                    securityReview.Scope,
                    securityReview.Scope == "new_code"
                    && comparison.RootElement.TryGetProperty("baseline_id", out var baseline)
                        ? baseline.GetString()
                        : null,
                    securityReview.Scope == "new_code" ? sourceDiff.BaseSha : null,
                    securityReview.Scope == "new_code" ? sourceDiff.HeadSha : null,
                    securityReview.Numerator,
                    securityReview.Denominator,
                    securityReview.Reason
                ),
            ];
        var complete =
            completeTools
            && profile.RequiredMetrics.All(m =>
                metrics.GetValueOrDefault(m) is not null
                || conditions.Any(c => c.Metric == m)
                    && conditions.Where(c => c.Metric == m).All(c => c.Result == "not_applicable")
            )
            && !manifest.Reports.Any(r => r.State is "failed" or "absent")
            && configuration.State is not ("missing" or "drift")
            && (
                profile.SecurityReview.MinimumHotspotReviewPercent is null
                || overallSecurityReview.Result != "unknown"
            );
        row.QualityGate =
            configuration.State is "missing" or "drift" ? "unknown"
            : conditions.Any(x => x.Result == "fail")
            || profile.SecurityReview.MinimumHotspotReviewPercent is not null
                && securityReview.Result == "fail"
                ? "fail"
            : !complete
            || conditions.Length == 0 && profile.SecurityReview.MinimumHotspotReviewPercent is null
            || conditions.Any(x => x.Result == "unknown")
                ? "unknown"
            : "pass";
        row.TechnicalState = complete ? "complete" : "incomplete";
        var formula =
            usesMqrRatings
            ? MqrRatings.FormulaVersion
            : profile.RuleCatalog.Length > 0
            || profile.AnalyzerConfigurations.Length > 0
            || scope.Definition is not null
            || profile.SecurityReview.ApplyApprovedExceptions
            || profile.SecurityReview.MinimumHotspotReviewPercent is not null
            || codeData.Reports.Any(r =>
                r.BranchesMeasured
                || r.ConditionsMeasured
                || r.DuplicationMeasured
                || r.Metrics.Count > 0
            )
                ? "imports/3"
            : profile.Conditions.Any(c => c.Scope == "new_code") ? "imports/2"
            : "imports/1";
        var remediation = ExecutableProfiles.EstimateRemediation(
            profile,
            ruleCatalog,
            findings.Where(f => !f.Excepted),
            manifest
        );
        row.GateSnapshot = JsonSerializer.Serialize(
            new
            {
                formula_version = formula,
                profile.ProfileId,
                profile_version = profile.Version,
                conditions,
                metrics,
                ratings,
                configuration,
                security_review = securityReview,
                security_reviews = new { overall = overallSecurityReview, new_code = newCodeSecurityReview },
                remediation,
                code_scope = scope,
                code_quality = new { global = globalQuality, new_code = newQuality },
                exceptions = exceptions.Select(x => new
                {
                    x.Id,
                    x.Tool,
                    x.RuleId,
                    x.Path,
                    x.Reason,
                    x.ExpiresAt,
                }),
                technical_state = row.TechnicalState == "complete" ? "success" : row.TechnicalState,
            },
            ApiJson.Options
        );
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await IngestionEndpoints.LockAsync(db, row.BuildKey, ct);
        if (
            !await db.Set<DurableJob>()
                .AnyAsync(
                    x =>
                        x.ResourceId == id
                        && x.Kind == "ingest"
                        && x.LeaseOwner == leaseOwner
                        && x.LeaseUntil > DateTime.UtcNow,
                    ct
                )
        )
            throw new OperationCanceledException("Job lease lost");
        await db.Set<FindingOccurrence>().Where(x => x.IngestionId == id).ExecuteDeleteAsync(ct);
        await db.Set<CoverageLine>().Where(x => x.IngestionId == id).ExecuteDeleteAsync(ct);
        await db.Set<SbomComponent>().Where(x => x.IngestionId == id).ExecuteDeleteAsync(ct);
        // ponytail: one report is capped at 250k normalized entries; bulk COPY is the upgrade path if measured import latency requires it.
        db.AddRange(findings);
        db.AddRange(components);
        db.AddRange(
            coverage.Select(c => new CoverageLine
            {
                IngestionId = id,
                Path = c.Key.Path,
                Line = c.Key.Line,
                Covered = c.Value,
            })
        );
        row.State = "completed";
        row.CompletedAt = DateTime.UtcNow;
        row.Error = "";
        var later = await db.Set<IngestionRecord>()
            .AnyAsync(
                x =>
                    x.AnalysisId == row.AnalysisId
                    && x.Revision > row.Revision
                    && x.State == "completed",
                ct
            );
        if (!later)
        {
            var analysis = await db.Set<AnalysisRecord>().FindAsync([row.AnalysisId], ct);
            if (analysis is null)
            {
                analysis = new AnalysisRecord
                {
                    AnalysisId = row.AnalysisId,
                    CreatedAt = ApiJson.Iso(row.CreatedAt),
                };
                db.Add(analysis);
            }
            analysis.ProjectKey = row.ProjectKey;
            analysis.Branch = manifest.Branch;
            analysis.CommitSha = manifest.CommitSha;
            analysis.CiProvider = manifest.CiProvider;
            analysis.Status = "completed";
            analysis.QualityGate = row.QualityGate;
            analysis.Completeness = complete ? "complete" : "incomplete";
            analysis.CompletedAt = row.CompletedAt;
            analysis.RepositoryUrl = manifest.RepositoryUrl;
            analysis.ProviderRunId = manifest.BuildId;
            analysis.ProfileId = profile.ProfileId;
            analysis.ProfileVersion = profile.Version;
            analysis.FormulaVersion = formula;
            analysis.GateSnapshot = row.GateSnapshot;
            analysis.SecurityRating = ratings?.Overall.Security.Rating;
            analysis.ReliabilityRating = ratings?.Overall.Reliability.Rating;
            analysis.MaintainabilityRating = ratings?.Overall.Maintainability.Rating;
            analysis.SecurityReviewRating = ratings?.Overall.SecurityReview.Rating;
            analysis.Bugs = Int("bugs");
            analysis.CodeSmells = Int("code_smells");
            analysis.Vulnerabilities = Int("vulnerabilities");
            analysis.SecurityHotspots = Int("security_hotspots");
            analysis.Coverage = Int("coverage");
            analysis.Duplication = Int("duplicated_lines_density");
            analysis.DependencyVulnerabilities = Int("dependency_vulnerabilities");
            analysis.Measurements = JsonSerializer.Serialize(
                metrics.ToDictionary(
                    x => x.Key,
                    x => new MeasurementValue(
                        metricStates[x.Key],
                        x.Value is >= int.MinValue and <= int.MaxValue ? (int)x.Value.Value : null,
                        "normalized-reports",
                        formula
                    )
                ),
                ApiJson.Options
            );
            analysis.ToolExecutions = JsonSerializer.Serialize(
                manifest.Reports.Select(r => new
                {
                    tool = r.Tool,
                    version = r.ToolVersion,
                    status = r.State == "measured" ? "success" : r.State,
                }),
                ApiJson.Options
            );
            analysis.GateFailures = JsonSerializer.Serialize(
                conditions
                    .Where(x => x.Result is "fail" or "unknown")
                    .Select(x =>
                        $"{x.Scope}/{x.Metric}: {x.Value?.ToString() ?? "not measured"} {x.Operator} {x.Threshold} ({x.Result})"
                    )
                    .Concat(
                        configuration.State is "missing" or "drift"
                            ? [$"configuration: {configuration.Reason}"]
                            : Array.Empty<string>()
                    )
            );
            analysis.PrId = manifest.PrId;
            analysis.PrRepoId = manifest.PrRepoId;
            analysis.PrAdoProject = manifest.PrAdoProject;
            analysis.PrAdoOrgUrl = manifest.PrAdoOrgUrl;
            analysis.PrSourceSha = manifest.SourceSha;
            analysis.PrTargetSha = manifest.TargetSha;
            analysis.PrTargetBranch = string.IsNullOrEmpty(manifest.PrId) ? "" : manifest.PrTargetBranch ?? "";
            analysis.ErrorCount = findings.Count(x =>
                !x.Excepted
                && x.Severity.ToLowerInvariant() is "blocker" or "critical" or "error" or "high"
            );
            analysis.WarningCount = findings.Count(x =>
                !x.Excepted && x.Severity.ToLowerInvariant() is "warning" or "medium"
            );
            analysis.InfoCount =
                findings.Count(x => !x.Excepted) - analysis.ErrorCount - analysis.WarningCount;
            analysis.QualityGateSummary = GateEvaluation.Summary(
                analysis.QualityGate,
                analysis.ErrorCount
            );
            analysis.DecorationRevision++;
            analysis.PrDecorationStatus = "queued";
            await FindingCollaboration.ObserveAsync(db, analysis, findings, ct, manifest.GitDiff);
            db.Add(
                new DurableJob
                {
                    Kind = "pr-decoration",
                    ResourceId = $"{analysis.AnalysisId}:{analysis.DecorationRevision}",
                }
            );
            var channels = await db.Set<NotificationChannel>()
                .Where(x =>
                    x.Enabled
                    && (
                        row.QualityGate == "fail" && x.OnFail
                        || row.QualityGate == "pass" && x.OnPass
                    )
                )
                .Select(x => x.Id)
                .ToListAsync(ct);
            foreach (var channel in channels)
                db.Add(new DurableJob { Kind = "notification", ResourceId = id + ":" + channel });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        int? Int(string key) => metrics.GetValueOrDefault(key) is { } value ? checked((int)value) : null;
    }

    public sealed record ConditionResult(
        string Metric,
        string Operator,
        bool Required,
        double Threshold,
        double? Value,
        string State,
        string Result,
        string Provenance,
        string Scope,
        string? BaselineId,
        string? BaseSha,
        string? HeadSha,
        double? Numerator,
        long? Denominator,
        string? Reason
    ,
        string? DisplayValue = null,
        string? DisplayThreshold = null
    );

    private static (
        double? Value,
        string State,
        double? Numerator,
        long? Denominator
    ) QualityMetric(CodeQualityTotals totals, string metric)
    {
        var ratio = metric switch
        {
            "coverage" => CombinedCoverage(totals),
            "branch_coverage" => totals.Branches,
            "condition_coverage" => totals.Conditions,
            "duplicated_lines_density" => totals.DuplicatedLinesDensity,
            _ => null,
        };
        if (ratio is not null)
            return (ratio.Percentage, ratio.State, ratio.Numerator, ratio.Denominator);
        double? value = metric switch
        {
            "duplicate_blocks" => totals.DuplicateBlocks,
            "duplicated_lines" => totals.DuplicatedLines,
            "new_lines" => totals.NewLines,
            "code_lines" => totals.CodeLines,
            "complexity" => totals.ClassComplexity,
            _ => null,
        };
        return (
            value,
            metric is "duplicate_blocks" or "duplicated_lines" ? totals.DuplicationState
            : value is null ? "not_measured"
            : "measured",
            value,
            null
        );
    }

    private static CodeRatio CombinedCoverage(CodeQualityTotals totals)
    {
        if (totals.Lines.State is not ("measured" or "not_applicable")) return totals.Lines;
        if (totals.Conditions.State == "not_measured") return totals.Lines;
        if (totals.Conditions.State is not ("measured" or "not_applicable")) return totals.Conditions;
        var numerator = (totals.Lines.Numerator ?? 0) + (totals.Conditions.Numerator ?? 0);
        var denominator = (totals.Lines.Denominator ?? 0) + (totals.Conditions.Denominator ?? 0);
        return denominator == 0
            ? new("not_applicable", 0, 0, null)
            : new("measured", numerator, denominator, 100d * numerator / denominator);
    }

    public static int? FindingCount(
        ProfileDefinition profile,
        RuleCatalogIndex ruleCatalog,
        IngestionManifest manifest,
        List<FindingOccurrence> findings,
        bool completeTools,
        string category
    )
    {
        if (ruleCatalog.HasAmbiguities) return null;
        var rules = ruleCatalog
            .RulesInCategory(category)
            .Select(r => (r.Tool, r.RuleId))
            .Concat(
                profile
                    .Rules.Where(r =>
                        r.Category == category
                        && ruleCatalog.Resolve(r.Tool, r.RuleId) is null
                    )
                    .Select(r => (r.Tool, r.RuleId))
            )
            .ToArray();
        return
            completeTools
            && rules.Length > 0
            && rules.All(rule =>
                manifest.Reports.Any(r =>
                    r.Tool == rule.Tool
                    && r.Format == "sarif"
                    && r.State == "measured"
                )
            )
            && manifest.Reports.Where(r => r.Format == "sarif").All(r => r.State == "measured")
            ? findings.Count(f => !f.Excepted && f.Category == category)
            : null;
    }

    public static (string Category, string Severity) ClassifyFinding(ProfileDefinition profile, RuleCatalogIndex ruleCatalog, ReportDescriptor descriptor, ImportedFinding finding)
    {
        var rule = ruleCatalog.Resolve(descriptor.Tool, finding.RuleId);
        var category = rule?.Category ?? (profile.RuleCatalog.Length > 0
            ? finding.Category == "secret" ? "secret" : "unknown"
            : profile.Rules.FirstOrDefault(item => item.Tool == descriptor.Tool && (item.RuleId == finding.RuleId || item.RuleId == "*"))?.Category
                ?? finding.Category);
        return (category, rule?.Severity ?? (profile.RuleCatalog.Length > 0 ? "unknown" : finding.Severity));
    }

    public static bool ProfilesCompatible(string baseline, string current) =>
        JsonSerializer.Serialize(ProfileCompatibilitySnapshot(baseline), ApiJson.Options)
        == JsonSerializer.Serialize(ProfileCompatibilitySnapshot(current), ApiJson.Options);

    private static ProfileDefinition? ProfileCompatibilitySnapshot(string json)
    {
        var profile = JsonSerializer.Deserialize<ProfileDefinition>(json, ApiJson.Options);
        return profile is null ? null : profile with
        {
            RuleCatalog = profile.RuleCatalog.Select(rule => rule with { ToolVersion = "" }).ToArray(),
            AnalyzerConfigurations = profile.AnalyzerConfigurations.Select(config => config with { ToolVersion = "" }).ToArray(),
            DisabledRules = profile.DisabledRules.Select(rule => rule with { ToolVersion = "" }).ToArray(),
        };
    }

    public static bool ProducersCompatible(IngestionManifest baseline, IngestionManifest current)
    {
        static HashSet<(string Tool, string Format, string FormatVersion)> Producers(IngestionManifest manifest) =>
            manifest.Reports.Select(report => (report.Tool, report.Format, report.FormatVersion)).ToHashSet();
        return Producers(baseline).SetEquals(Producers(current));
    }

    public static ConditionResult[] EvaluateConditions(
        ProfileDefinition profile,
        Dictionary<string, double?> metrics,
        Dictionary<string, string> states,
        JsonElement comparison,
        GitDiff diff,
        int coveredLines,
        int executableLines,
        bool coverageMeasured,
        CodeQualityTotals? globalQuality = null,
        CodeQualityTotals? newQuality = null
    ,
        MqrRatingsSnapshot? ratings = null
    )
    {
        var usesMqrRatings = profile.Conditions.Any(condition =>
            condition.Metric.EndsWith("_rating", StringComparison.Ordinal)
        );
        return profile
            .Conditions.Select(c =>
            {
                var value = metrics.GetValueOrDefault(c.Metric);
                var state = states.GetValueOrDefault(c.Metric, "not_measured");
                string? reason = null,
                    baseline = null;
                double? numerator = value;
                long? denominator = c.Metric == "coverage" ? executableLines : null;
                var ratingMetric = c.Metric is "security_rating" or "reliability_rating" or "maintainability_rating" or "security_review_rating";
                var qualityMetric =
                    c.Metric
                    is "branch_coverage"
                        or "condition_coverage"
                        or "duplicate_blocks"
                        or "duplicated_lines"
                        or "duplicated_lines_density"
                        or "code_lines"
                        or "complexity"
                    || usesMqrRatings && c.Metric == "coverage";
                MqrRating? rating = null;
                if (ratingMetric && ratings is not null)
                {
                    var scopeRatings = c.Scope == "new_code" ? ratings.NewCode : ratings.Overall;
                    rating = c.Metric switch
                    {
                        "security_rating" => scopeRatings.Security,
                        "reliability_rating" => scopeRatings.Reliability,
                        "maintainability_rating" => scopeRatings.Maintainability,
                        _ => scopeRatings.SecurityReview,
                    };
                    value = rating.Rank;
                    state = rating.State;
                    reason = rating.Reason;
                }
                if (qualityMetric && globalQuality is not null)
                {
                    var quality = QualityMetric(globalQuality, c.Metric);
                    (value, state, numerator, denominator) = quality;
                }
                if (c.Metric == "coverage")
                    numerator = value is null ? null : coveredLines;
                var allowedNotApplicableReason = false;
                if (c.Scope == "new_code")
                {
                    value = null;
                    numerator = null;
                    denominator = null;
                    if (comparison.GetProperty("state").GetString() != "available")
                        reason = comparison.TryGetProperty("reason", out var why)
                            ? why.GetString()
                            : "comparison_unavailable";
                    else
                    {
                        baseline = comparison.GetProperty("baseline_id").GetString();
                        if ((c.Metric is "coverage" or "duplicated_lines_density")
                            && profile.SmallChangesThreshold is { } smallChangeThreshold
                            && newQuality?.NewLines < smallChangeThreshold)
                        {
                            state = "not_applicable";
                            reason = $"small_change_less_than_{smallChangeThreshold}_lines";
                        }
                        else if (ratingMetric && rating is not null)
                        {
                            value = rating.Rank;
                            state = rating.State;
                            reason = rating.Reason;
                            numerator = value;
                        }
                        else if (usesMqrRatings && c.Metric == "coverage" && coverageMeasured && newQuality is not null)
                        {
                            (value, state, numerator, denominator) = QualityMetric(newQuality, c.Metric);
                            if (state == "not_applicable" && denominator == 0)
                                reason = "no_changed_executable_lines";
                            else if (value is null)
                                reason = "coverage_not_measured_or_incomplete";
                        }
                        else if (c.Metric == "coverage" && coverageMeasured)
                        {
                            if (!comparison.GetProperty("coverage_paths_complete").GetBoolean())
                                reason = "changed_files_missing_from_coverage";
                            else
                            {
                                denominator = comparison.GetProperty("executable_lines").GetInt32();
                                numerator = comparison.GetProperty("covered_lines").GetInt32();
                                if (denominator == 0)
                                {
                                    state = "not_applicable";
                                    reason = "no_changed_executable_lines";
                                }
                                else
                                {
                                    value = 100.0 * numerator / denominator;
                                    state = "measured";
                                }
                            }
                        }
                        else if (qualityMetric && newQuality is not null)
                        {
                            (value, state, numerator, denominator) = QualityMetric(
                                newQuality,
                                c.Metric
                            );
                            if (state == "not_applicable")
                                reason = "no_changed_elements_in_scope";
                            else if (value is null)
                                reason = "metric_not_measured_or_ambiguous";
                        }
                        else if (
                            c.Metric
                                is "bugs"
                                    or "code_smells"
                                    or "vulnerabilities"
                                    or "security_hotspots"
                            && metrics.GetValueOrDefault(c.Metric) is not null
                        )
                        {
                            if (
                                comparison
                                    .GetProperty("indeterminate_metrics")
                                    .EnumerateArray()
                                    .Any(m => m.GetString() == c.Metric)
                            )
                                reason = "finding_identity_indeterminate";
                            else
                            {
                                value = comparison
                                    .GetProperty("new_metrics")
                                    .TryGetProperty(c.Metric, out var count)
                                    ? count.GetInt32()
                                    : 0;
                                numerator = value;
                                state = "measured";
                            }
                        }
                        else
                            reason = "metric_not_measured";
                    }
                    allowedNotApplicableReason =
                        reason?.StartsWith("small_change_less_than_", StringComparison.Ordinal) == true
                        || reason is "no_changed_executable_lines" or "no_changed_elements_in_scope";
                    if (
                        value is null
                        && (state != "not_applicable" || !allowedNotApplicableReason)
                    )
                        state = "not_measured";
                }
                var allowedNotApplicable = state == "not_applicable" && allowedNotApplicableReason;
                var result =
                    allowedNotApplicable ? "not_applicable"
                    : value is not { } number ? "unknown"
                    : (c.Operator == "max" ? number <= c.Threshold : number >= c.Threshold) ? "pass"
                    : "fail";
                return new ConditionResult(
                    c.Metric,
                    c.Operator == "max" ? "<=" : ">=",
                    true,
                    c.Threshold,
                    value,
                    state,
                    result,
                    ratingMetric ? MqrRatings.FormulaVersion
                        : qualityMetric ? "normalized-reports/code-quality/sonar-mqr-v1"
                        : c.Scope == "new_code" ? "normalized-reports/git-diff/v2"
                        : "normalized-reports/v1",
                    c.Scope,
                    baseline,
                    c.Scope == "new_code" ? diff.BaseSha : null,
                    c.Scope == "new_code" ? diff.HeadSha : null,
                    numerator,
                    denominator,
                    reason
                ,
                    rating?.Rating,
                    ratingMetric ? "A" : null
                );
            })
            .ToArray();
    }

    private async Task<string> CompareAsync(
        IngestionRecord row,
        IngestionManifest manifest,
        List<FindingOccurrence> current,
        Dictionary<(string Path, int Line), bool> coverage,
        bool completeTools,
        CancellationToken ct
    )
    {
        foreach (var finding in current)
            finding.ComparisonState = finding.Excepted ? "excepted" : null;
        NewCodeResolution? newCode = null;
        try
        {
            newCode = JsonSerializer.Deserialize<NewCodeResolution>(row.NewCodeSnapshot, ApiJson.Options);
        }
        catch (JsonException)
        {
            // Old pending rows predate the dedicated snapshot and keep the legacy exact-target lookup.
        }
        string Unavailable(string reason) => JsonSerializer.Serialize(
            new
            {
                state = "unavailable",
                reason,
                effective_definition = newCode?.EffectiveDefinition,
                effective_source = newCode?.EffectiveSource,
                baseline_id = newCode?.BaselineIngestionId,
                baseline_analysis_id = newCode?.BaselineAnalysisId,
                baseline_sha = newCode?.BaselineSha,
                baseline_branch = newCode?.BaselineBranch,
                baseline_version = newCode?.BaselineVersion,
                baseline_date = newCode?.BaselineDate,
            },
            ApiJson.Options
        );
        var scope = CodeScopes.Read(row.CodeScopeSnapshot);
        if (newCode?.Reason is not null)
            return Unavailable(newCode.Reason);
        var baselineSha = newCode?.BaselineSha ?? manifest.TargetSha;
        if (string.IsNullOrEmpty(baselineSha))
            return Unavailable("baseline_absent");
        if (baselineSha != manifest.TargetSha)
            return Unavailable("configured_baseline_sha_mismatch");
        if (
            manifest.GitDiff.State != "available"
            || manifest.GitDiff.BaseSha != manifest.TargetSha
            || manifest.GitDiff.HeadSha != manifest.CommitSha
        )
            return Unavailable("git_history_unavailable");
        var candidates =
            from i in db.Set<IngestionRecord>().AsNoTracking()
            join a in db.Set<AnalysisRecord>() on i.AnalysisId equals a.AnalysisId
            where
                i.ProjectKey == row.ProjectKey
                && a.CommitSha == baselineSha
                && i.State == "completed"
            orderby i.CreatedAt descending, i.Revision descending
            select i;
        // ponytail: a first New Code run can be incomplete only because it has no baseline.
        // The exact profile, producer set and measured reports below remain the safety gate;
        // persist a dedicated baseline-eligibility flag if more incomplete states are introduced.
        IngestionRecord? baseline = null;
        IngestionManifest? baselineManifest = null;
        await foreach (var candidate in candidates.AsAsyncEnumerable().WithCancellation(ct))
        {
            var origin = JsonSerializer.Deserialize<IngestionManifest>(
                candidate.Manifest,
                ApiJson.Options
            );
            if (
                newCode?.BaselineIngestionId is not null
                && candidate.Id != newCode.BaselineIngestionId
            )
                continue;
            if (
                origin is null
                || origin.CommitSha != baselineSha
                || !string.Equals(
                    origin.RepositoryUrl.TrimEnd('/'),
                    manifest.RepositoryUrl.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;
            baseline = candidate;
            baselineManifest = origin;
            break;
        }
        if (baseline is null)
            return Unavailable("baseline_absent");
        if (baseline.DetailsPurged)
            return Unavailable("baseline_details_purged");
        if (!ProfilesCompatible(baseline.ProfileSnapshot, row.ProfileSnapshot))
            return Unavailable("profiles_incompatible");
        if (
            CodeScopes.CompatibilityKey(CodeScopes.Read(baseline.CodeScopeSnapshot).Definition)
                != CodeScopes.CompatibilityKey(scope.Definition)
        )
            return Unavailable("code_scopes_incompatible");
        var profile = JsonSerializer.Deserialize<ProfileDefinition>(row.ProfileSnapshot, ApiJson.Options);
        if (profile?.Conditions.Any(x => x.Scope == "new_code" && x.Metric == "coverage") == true
            && (!manifest.Reports.Any(x => x.Format is "lcov" or "cobertura")
                || manifest.Reports.Where(x => x.Format is "lcov" or "cobertura").Any(x => x.State != "measured")
                || !baselineManifest!.Reports.Any(x => x.Format is "lcov" or "cobertura")
                || baselineManifest.Reports.Where(x => x.Format is "lcov" or "cobertura").Any(x => x.State != "measured")))
            return Unavailable("coverage_producer_incomplete");
        if (
            !completeTools
            || manifest.Reports.Any(x => x.State != "measured")
            || baselineManifest!.Reports.Any(x => x.State != "measured")
        )
            return Unavailable("producers_incomplete");
        if (!ProducersCompatible(manifest, baselineManifest))
            return Unavailable("producers_incompatible");
        var old = await db.Set<FindingOccurrence>()
            .AsNoTracking()
            .Where(x => x.IngestionId == baseline.Id && !x.Excepted)
            .ToListAsync(ct);
        var includedDiff = manifest.GitDiff with
        {
            Files = manifest
                .GitDiff.Files.Where(x => CodeScopes.Includes(scope.Definition, x.Path))
                .ToList(),
        };
        var sourceDiff = manifest.GitDiff with
        {
            Files = manifest
                .GitDiff.Files.Where(x => CodeScopes.IsSource(scope.Definition, x.Path))
                .ToList(),
        };
        return Compare(
            includedDiff,
            old,
            current,
            coverage,
            baseline.Id,
            sourceDiff,
            newCode
        );
    }

    public static string Compare(
        GitDiff diff,
        List<FindingOccurrence> old,
        List<FindingOccurrence> current,
        Dictionary<(string Path, int Line), bool> coverage,
        string baselineId,
        GitDiff? coverageDiff = null,
        NewCodeResolution? resolution = null
    )
    {
        old = old.Where(x => !x.Excepted).ToList();
        current = current.Where(x => !x.Excepted).ToList();
        var files = diff.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var byFingerprint = old.Where(x => !string.IsNullOrEmpty(x.Fingerprint))
            .ToLookup(x => (x.Tool, x.RuleId, x.Fingerprint));
        // ponytail: a single rule/file with many un-fingerprinted findings remains quadratic; add a mapped-line index if benchmarks require it.
        var byPath = old.ToLookup(x => (x.Tool, x.RuleId, x.Path));
        var used = new HashSet<long>();
        var ambiguous = new HashSet<long>();
        var states = new List<object>();
        int added = 0,
            persistent = 0,
            indeterminate = 0;
        var newMetrics = new Dictionary<string, int>();
        var uncertainMetrics = new HashSet<string>();
        foreach (var finding in current)
        {
            var file = finding.Path is null ? null : files.GetValueOrDefault(finding.Path);
            var samePath = byPath[(finding.Tool, finding.RuleId, file?.OldPath ?? finding.Path)]
                .ToList();
            var candidates = !string.IsNullOrEmpty(finding.Fingerprint)
                ? byFingerprint[(finding.Tool, finding.RuleId, finding.Fingerprint)].ToList()
                : [];
            var fingerprintCollision = candidates.Count > 1;
            if (fingerprintCollision)
                candidates = candidates
                    .Where(candidate =>
                        samePath.Contains(candidate)
                        && candidate.Message == finding.Message
                        && (
                            file is null
                                ? candidate.Line == finding.Line
                                : candidate.Line is { } line
                                    && file.LineMap.GetValueOrDefault(line.ToString())
                                        == finding.Line
                        )
                    )
                    .ToList();
            if (candidates.Count == 0 && !fingerprintCollision)
                candidates = samePath
                    .Where(f =>
                        f.Message == finding.Message
                        && (
                            string.IsNullOrEmpty(f.Fingerprint)
                            || string.IsNullOrEmpty(finding.Fingerprint)
                            || f.Fingerprint == finding.Fingerprint
                        )
                        && (
                            file is null
                                ? f.Line == finding.Line
                                : f.Line is { } line
                                    && file.LineMap.GetValueOrDefault(line.ToString())
                                        == finding.Line
                        )
                    )
                    .ToList();
            string state;
            var match = candidates.FirstOrDefault(candidate => !used.Contains(candidate.Id));
            if (match is not null && used.Add(match.Id))
            {
                state = "persistent";
                persistent++;
            }
            else if (
                candidates.Count == 0
                && !fingerprintCollision
                && finding.Line is { } n
                && file?.AddedLines.Contains(n) == true
                && !samePath.Any(f =>
                    f.Message == finding.Message
                    && (
                        string.IsNullOrEmpty(f.Fingerprint)
                        || string.IsNullOrEmpty(finding.Fingerprint)
                    )
                )
            )
            {
                state = "new";
                added++;
            }
            else
            {
                state = "indeterminate";
                indeterminate++;
                // Missing location evidence must not turn the possible prior occurrence into a resolution.
                foreach (var candidate in candidates.Count > 0 ? candidates : samePath)
                    ambiguous.Add(candidate.Id);
            }
            var metric = finding.Category switch
            {
                "bug" => "bugs",
                "code_smell" => "code_smells",
                "vulnerability" => "vulnerabilities",
                "security_hotspot" => "security_hotspots",
                _ => "unknown",
            };
            finding.ComparisonState = state;
            if (state == "new")
                newMetrics[metric] = newMetrics.GetValueOrDefault(metric) + 1;
            if (state == "indeterminate")
                uncertainMetrics.Add(metric);
            // Summary comparison is bounded; finding detail remains in its paginated endpoint.
            if (states.Count < 100)
                states.Add(
                    new
                    {
                        finding.Tool,
                        finding.RuleId,
                        finding.Path,
                        finding.Line,
                        state,
                    }
                );
        }
        var resolved = old.Count(f => !used.Contains(f.Id) && !ambiguous.Contains(f.Id));
        var coverageChanges = coverageDiff ?? diff;
        var changedLines = coverageChanges
            .Files.SelectMany(f => f.AddedLines.Select(line => (f.Path, Line: line)))
            .ToHashSet();
        var lines = coverage.Where(c => changedLines.Contains(c.Key)).ToList();
        return JsonSerializer.Serialize(
            new
            {
                state = "available",
                baseline_id = baselineId,
                baseline_analysis_id = resolution?.BaselineAnalysisId,
                baseline_sha = resolution?.BaselineSha ?? diff.BaseSha,
                baseline_branch = resolution?.BaselineBranch,
                baseline_version = resolution?.BaselineVersion,
                baseline_date = resolution?.BaselineDate,
                effective_definition = resolution?.EffectiveDefinition,
                effective_source = resolution?.EffectiveSource,
                reason = resolution?.Reason,
                new_count = added,
                persistent_count = persistent,
                resolved_count = resolved,
                indeterminate_count = indeterminate,
                new_metrics = newMetrics,
                indeterminate_metrics = uncertainMetrics,
                base_sha = diff.BaseSha,
                head_sha = diff.HeadSha,
                coverage_paths_complete = true,
                coverage_state = lines.Count == 0 ? "not_applicable" : "measured",
                covered_lines = lines.Count(x => x.Value),
                executable_lines = lines.Count,
                coverage = lines.Count == 0
                    ? (double?)null
                    : 100.0 * lines.Count(x => x.Value) / lines.Count,
                findings = states,
                findings_truncated = current.Count > states.Count,
            },
            ApiJson.Options
        );
    }
}
