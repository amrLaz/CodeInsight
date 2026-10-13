using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;

public sealed class AnalysisTests
{

[Fact]
    public void All() => Run();

    private static void Run()
    {
        var request = new AnalysisRequest
        {
            ProjectKey = "example",
            CommitSha = "abcdef",
            Branch = "main",
            CiProvider = "local",
            ProfileId = "p0-dotnet",
            ProfileVersion = "1",
            FormulaVersion = "1",
        };
        int[] values = [50, 50, 50, 100, 10, 10, 10];
        for (var i = 0; i < values.Length; i++)
            request.Measurements[GateEvaluation.GateMetrics[i]] = new(
                "measured",
                values[i],
                "semgrep",
                "1"
            );
        request.ToolExecutions =
        [
            new("semgrep", "success"),
            new("trivy", "success"),
            new("gitleaks", "success"),
        ];
        var project = new Project { AnalysisProfileId = "p0-dotnet", AnalysisProfileVersion = "1" };
        var gate = new QualityGateConfig
        {
            Id = "default",
            Name = "Default",
            MinMaintainabilityScore = 50,
            MinSecurityScore = 50,
            MinReliabilityScore = 50,
            MaxCodeSmells = 100,
            MaxVulnerabilities = 10,
            MaxSecurityHotspots = 10,
            MaxBugs = 10,
        };
        GateEvaluation.Validate(request);
        Assert.True(GateEvaluation.Completeness(request, project) == "complete", "complete profile");
        var metrics = GateEvaluation.BuildMetrics(request);
        Assert.True(
            GateEvaluation
                .Evaluate(metrics, gate, request.Measurements, "p0-dotnet", "1", "1")
                .Verdict == "pass",
            "inclusive thresholds"
        );
        for (var i = 0; i < values.Length; i++)
        {
            var name = GateEvaluation.GateMetrics[i];
            metrics.Set(name, values[i] + (i < 3 ? -1 : 1));
            Assert.True(
                GateEvaluation
                    .Evaluate(metrics, gate, request.Measurements, "p0-dotnet", "1", "1")
                    .Verdict == "fail",
                "threshold " + name
            );
            metrics.Set(name, values[i]);
        }
        request.Measurements.Remove("bugs");
        Assert.True(
            GateEvaluation.Completeness(request, project) == "incomplete",
            "missing required metric"
        );
        Assert.True(
            GateEvaluation
                .Evaluate(metrics, gate, request.Measurements, "p0-dotnet", "1", "1")
                .Verdict == "unknown",
            "missing is not zero"
        );
        request.Measurements["bugs"] = new("measured", 0);
        Assert.True(GateEvaluation.BuildMetrics(request).Bugs == 0, "measured zero");
        request.FormulaVersion = "unrecognized";
        Assert.True(GateEvaluation.Completeness(request, project) == "incomplete", "unknown formula");
        request.FormulaVersion = "1";
        request.ToolExecutions.Add(new("trivy", "failed"));
        Assert.True(GateEvaluation.Completeness(request, project) == "incomplete", "failed tool");
        Assert.True(
            GateEvaluation.TechnicalState(
                "incomplete",
                request.ToolExecutions,
                request.Measurements
            ) == "failed",
            "failed technical state"
        );
        request.Measurements.Clear();
        Assert.True(
            GateEvaluation.Completeness(request, project) == "incomplete",
            "empty metrics incomplete"
        );
        Assert.True(GateEvaluation.BuildMetrics(request).SecurityScore == null, "absence preserved");
        request.Measurements["security_score"] = new("measured");
        try
        {
            GateEvaluation.Validate(request);
            throw new InvalidOperationException("Invalid measurement accepted");
        }
        catch (ApiException) { }
        request.Measurements["security_score"] = new("measured", 100);
        request.Measurements["vulnerabilities"] = new("measured", 0);
        request.ProfileId = "p0-iac";
        project.AnalysisProfileId = "p0-iac";
        request.ToolExecutions =
        [
            new("checkov", "success"),
            new("trivy", "success"),
            new("gitleaks", "success"),
        ];
        Assert.True(
            GateEvaluation.Completeness(request, project) == "complete",
            "IaC two required measurements"
        );
        var iac = GateEvaluation.Evaluate(
            GateEvaluation.BuildMetrics(request),
            gate,
            request.Measurements,
            "p0-iac",
            "1",
            "1"
        );
        Assert.True(iac.Verdict == "pass", "optional measurements do not block IaC");
        Assert.True(
            ((List<object>)iac.Snapshot["conditions"]!).Count == 7,
            "seven snapshot conditions retained"
        );
        var json = JsonSerializer.SerializeToNode(request, ApiJson.Options)!.AsObject();
        foreach (
            var field in new[]
            {
                "project_key",
                "commit_sha",
                "branch",
                "ci_provider",
                "profile_id",
                "profile_version",
                "formula_version",
                "measurements",
                "tool_executions",
            }
        )
        {
            var missing = json.DeepClone().AsObject();
            missing.Remove(field);
            try
            {
                missing.Deserialize<AnalysisRequest>(ApiJson.Options);
                throw new InvalidOperationException("Missing required field accepted: " + field);
            }
            catch (JsonException) { }
        }
        json["unexpected_field"] = 1;
        try
        {
            json.Deserialize<AnalysisRequest>(ApiJson.Options);
            throw new InvalidOperationException("Unknown field accepted");
        }
        catch (JsonException) { }
        request.ProfileId = "";
        try
        {
            GateEvaluation.Validate(request);
            throw new InvalidOperationException("Empty profile accepted");
        }
        catch (ApiException) { }
        Assert.True(
            AnalysisFindings.RepoFileUrl(
                "azure-devops",
                "https://user@dev.azure.com/org/project/_git/repo",
                "abcdef",
                "/mnt/vss/_work/1/s/a.cs",
                4,
                6
            )
                == "https://dev.azure.com/org/project/_git/repo?path=%2Fa.cs&version=GCabcdef&line=4&lineEnd=7&lineStartColumn=1&lineEndColumn=1&lineStyle=plain&_a=contents",
            "ADO source link"
        );
        Assert.True(AnalysisFindings.SeverityBucket("CRITICAL") == "blocker", "critical severity bucket");
        Assert.True(AnalysisFindings.SeverityBucket("warning") == "medium", "warning severity bucket");
        Assert.True(AnalysisFindings.SeverityBucket("convention") == "low", "convention severity bucket");
        Assert.True(AnalysisFindings.SeverityBucket("unknown") == "info", "unknown severity bucket");
        Assert.True(PromptContext.Extract("one\ntwo\nthree", 1, 1) == "    1: one\n    2: two", "prompt first line bounds");
        Assert.True(PromptContext.Extract("one\ntwo\nthree", 2, 1).Contains("    3: three"), "prompt middle line");
        Assert.True(PromptContext.Extract("one\ntwo\nthree", 3, 1) == "    2: two\n    3: three", "prompt last line bounds");
        Assert.True(AnalysisLifecycle.SafePath("src/file.cs") == "src/file.cs", "safe source path");
        try { AnalysisLifecycle.SafePath("../secret.txt"); throw new InvalidOperationException("Hostile source path accepted"); } catch (ApiException) { }
        Assert.True(
            AnalysisFindings.RepoFileUrl(
                "azure-devops",
                "https://example.com/_git/repo",
                "abcdef",
                "a.cs",
                1,
                1
            )
                is null,
            "source link host allowlist"
        );
        var record = new AnalysisRecord
        {
            AnalysisId = "12345678",
            ProjectKey = "a,b",
            CommitSha = "abcdef",
            Branch = "main",
            CiProvider = "local",
            Status = "completed",
            QualityGate = "unknown",
            CreatedAt = "2026-09-05T00:00:00+00:00",
            AffectedFiles = "{}",
            GateFailures = "[]",
            Measurements = "{}",
            ToolExecutions = "[]",
            GateSnapshot = "{}",
            Completeness = "incomplete",
            ProfileId = "p0-dotnet",
            ProfileVersion = "1",
            FormulaVersion = "1",
        };
        Assert.True(
            Encoding.UTF8.GetString(ExportService.Csv([record])).Contains("\"a,b\""),
            "CSV quoting"
        );
        Console.WriteLine(
            "Strict analysis contract, gates, IaC, null measurements, and CSV checks passed."
        );
    }

}
