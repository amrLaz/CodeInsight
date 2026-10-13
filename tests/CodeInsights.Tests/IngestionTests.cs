using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class IngestionTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        var url = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_URL");
        if (string.IsNullOrEmpty(connection) || string.IsNullOrEmpty(url))
        {
            Console.WriteLine(
                "P1 ingestion HTTP/PostgreSQL checks require CODEINSIGHTS_TEST_DB and CODEINSIGHTS_TEST_URL."
            );
            return;
        }
        var database = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(
            database.Host is "localhost" or "127.0.0.1"
                && database.Database
                    is "codeinsights_p1"
                        or "codeinsights_test"
                        or "codeinsights_checks",
            "use the explicitly isolated local test database"
        );
        Assert.True(
            Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback,
            "test API must be loopback"
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        using var client = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(30),
        };
        var admin = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_TOKEN");
        if (string.IsNullOrEmpty(admin))
        {
            var login = await Call(
                "POST",
                "/api/v1/auth/login",
                new
                {
                    email = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_EMAIL")
                        ?? "admin@codeinsights.local",
                    password = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_PASSWORD")
                        ?? "Isolated-bootstrap-123",
                }
            );
            admin = login.GetProperty("token").GetString()!;
        }
        var key = "check-p1-" + Guid.NewGuid().ToString("N")[..12];
        var profile = new ProfileDefinition
        {
            ProfileId = key,
            Version = "1",
            RequiredTools = ["fixture"],
            RequiredMetrics = ["bugs"],
            Rules = [new("fixture", "*", "bug")],
            Conditions = [new("bugs", "max", 0)],
        };
        await Call(
            "POST",
            "/api/v1/projects",
            new
            {
                key,
                name = key,
                analysis_profile_id = "p0-dotnet",
                analysis_profile_version = "1",
            },
            admin
        );
        await Call(
            "PUT",
            $"/api/v1/new-code/projects/{key}/main-branch",
            new { main_branch = "main" },
            admin
        );
        object Definition(string level, string mode, int? days = null, string? branch = null, string? reference = null, string? analysis = null) => new
        {
            level,
            project_key = level == "global" ? null : key,
            branch,
            mode,
            number_of_days = days,
            reference_branch = reference,
            analysis_id = analysis,
        };
        await Call("PUT", "/api/v1/new-code/definitions/global/global", Definition("global", "number_of_days", 1), admin);
        await Call("PUT", "/api/v1/new-code/definitions/global/global", Definition("global", "number_of_days", 90), admin);
        await Call("PUT", "/api/v1/new-code/definitions/global/global", Definition("global", "number_of_days", 0), admin, 422);
        await Call("PUT", "/api/v1/new-code/definitions/global/global", Definition("global", "number_of_days", 91), admin, 422);
        await Call("PUT", "/api/v1/new-code/definitions/global/global", Definition("global", "reference_branch", reference: "main"), admin, 422);
        await Call("PUT", $"/api/v1/new-code/definitions/project/project:{key}", Definition("project", "specific_analysis", analysis: "missing"), admin, 422);
        await Call("PUT", $"/api/v1/new-code/definitions/project/project:{key}", Definition("project", "reference_branch", reference: "main"), admin, 422);
        var branchScope = Uri.EscapeDataString($"branch:{key}:refs/heads/feature");
        await Call("PUT", $"/api/v1/new-code/definitions/branch/{branchScope}", Definition("branch", "reference_branch", branch: "feature", reference: "feature"), admin, 422);
        await Call("PUT", $"/api/v1/new-code/definitions/project/project:{key}", Definition("project", "number_of_days", 30), admin);
        var absentDays = await Call(
            "POST",
            "/api/v1/new-code/resolve",
            new { project_key = key, repository_url = "https://dev.azure.com/org/project/_git/repo", branch = "main", project_version = "1" },
            admin
        );
        Assert.True(absentDays.GetProperty("reason").GetString() == "number_of_days_baseline_absent", "number-of-days has no fallback when no old-enough snapshot exists");
        await Call("DELETE", $"/api/v1/new-code/definitions/project/project:{key}", token: admin, expected: 204);
        await Call("DELETE", "/api/v1/new-code/definitions/global/global", token: admin, expected: 204);
        await using (var newCodeDb = Db())
        {
            newCodeDb.Add(new ProjectIntegration
            {
                Id = Guid.NewGuid().ToString(),
                ProjectKey = key,
                RepoProvider = "azure-devops",
                RepoUrl = "https://dev.azure.com/org/project/_git/repo",
                PipelineProvider = "azure-devops",
            });
            newCodeDb.Add(new AnalysisRecord
            {
                AnalysisId = Guid.NewGuid().ToString(),
                ProjectKey = key,
                Branch = "refs/heads/feature",
                CommitSha = new string('c', 40),
                RepositoryUrl = "https://dev.azure.com/org/project/_git/other",
                Status = "completed",
                CompletedAt = DateTime.UtcNow,
                CreatedAt = ApiJson.Iso(DateTime.UtcNow),
            });
            await newCodeDb.SaveChangesAsync(ct);
            var wrongRepositoryAnalysis = await newCodeDb.Set<AnalysisRecord>()
                .Where(x => x.ProjectKey == key && x.RepositoryUrl.EndsWith("/other"))
                .Select(x => x.AnalysisId)
                .SingleAsync(ct);
            await Call(
                "PUT",
                $"/api/v1/new-code/definitions/branch/{branchScope}",
                Definition("branch", "specific_analysis", branch: "feature", analysis: wrongRepositoryAnalysis),
                admin,
                422
            );
            var baselineAnalysisId = Guid.NewGuid().ToString();
            newCodeDb.Add(new AnalysisRecord
            {
                AnalysisId = baselineAnalysisId,
                ProjectKey = key,
                Branch = "refs/heads/main",
                CommitSha = new string('a', 40),
                RepositoryUrl = "https://dev.azure.com/org/project/_git/repo",
                Status = "completed",
                CompletedAt = DateTime.UtcNow,
                CreatedAt = ApiJson.Iso(DateTime.UtcNow),
            });
            newCodeDb.Add(new IngestionRecord
            {
                Id = Guid.NewGuid().ToString(),
                AnalysisId = baselineAnalysisId,
                ProjectKey = key,
                BuildKey = Guid.NewGuid().ToString(),
                ManifestHash = Guid.NewGuid().ToString(),
                Manifest = JsonSerializer.Serialize(
                    new IngestionManifest
                    {
                        ProjectKey = key,
                        RepositoryUrl = "https://dev.azure.com/org/project/_git/repo",
                        Branch = "refs/heads/main",
                        CommitSha = new string('a', 40),
                        ProjectVersion = "0",
                    },
                    ApiJson.Options
                ),
                State = "completed",
                CompletedAt = DateTime.UtcNow,
            });
            await newCodeDb.SaveChangesAsync(ct);
        }
        await using (var legacyDb = Db())
        {
            var legacy = await IngestionEndpoints.ResolveNewCodeAsync(
                legacyDb,
                new IngestionManifest
                {
                    ProjectKey = key,
                    RepositoryUrl = "https://dev.azure.com/org/project/_git/repo",
                    Branch = "refs/heads/main",
                    CommitSha = new string('b', 40),
                    TargetSha = new string('a', 40),
                    GitDiff = new GitDiff
                    {
                        State = "available",
                        BaseSha = new string('a', 40),
                        HeadSha = new string('b', 40),
                    },
                },
                DateTime.UtcNow,
                ct
            );
            Assert.True(
                legacy.EffectiveSource == "legacy_manifest_target"
                    && legacy.BaselineSha == new string('a', 40),
                "legacy explicit target remains usable only while no dedicated New Code definition exists"
            );
        }
        await Call("PUT", $"/api/v1/new-code/definitions/project/project:{key}", Definition("project", "previous_version"), admin);
        var previousVersion = await Call(
            "POST",
            "/api/v1/new-code/resolve",
            new { project_key = key, repository_url = "https://dev.azure.com/org/project/_git/repo", branch = "main", project_version = "1" },
            admin
        );
        Assert.True(
            previousVersion.GetProperty("baseline_version").GetString() == "0"
                && previousVersion.GetProperty("baseline_sha").GetString() == new string('a', 40),
            "previous-version selects the latest completed snapshot of a distinct version"
        );
        await Call("POST", "/api/v1/profiles", profile, admin, 201);
        await Call(
            "PUT",
            $"/api/v1/profiles/projects/{key}",
            new ProfileAssignment(key, "1"),
            admin
        );
        var token = await Call(
            "POST",
            "/api/v1/platform/token/generate",
            new
            {
                token_name = key,
                project_keys = new[] { key },
                scopes = new[] { "analysis:read", "analysis:write" },
            },
            admin
        );
        var ci = token.GetProperty("token").GetString()!;
        await Call(
            "POST",
            "/api/v1/new-code/resolve",
            new { project_key = key, repository_url = "https://dev.azure.com/org/project/_git/repo", branch = "main", project_version = "1" },
            ci
        );
        var registerToken = await Call(
            "POST",
            "/api/v1/platform/token/generate",
            new
            {
                token_name = key + "-register",
                project_keys = new[] { key },
                scopes = new[] { "analysis:read", "analysis:write", "project:register" },
            },
            admin
        );
        var registerCi = registerToken.GetProperty("token").GetString()!;
        var autoKey = "check-p1-autoreg-" + Guid.NewGuid().ToString("N")[..12];
        await Call(
            "POST",
            "/api/v1/new-code/resolve",
            new
            {
                project_key = autoKey,
                repository_url = "https://dev.azure.com/org/project/_git/auto-register-repo",
                branch = "main",
                profile_id = "p0-dotnet",
                profile_version = "1",
            },
            registerCi
        );
        await using (var autoDb = Db())
        {
            var registered = await autoDb.Set<Project>().SingleOrDefaultAsync(x => x.Key == autoKey);
            Assert.True(
                registered is not null
                    && registered.AnalysisProfileId == "p0-dotnet"
                    && registered.AnalysisProfileVersion == "1",
                "new-code/resolve auto-registers an unknown project with the supplied profile when the token carries project:register"
            );
            var granted = await autoDb.Set<TokenProjectAccess>()
                .AnyAsync(x => x.ProjectKey == autoKey);
            Assert.True(
                granted,
                "the resolving CI token is granted access to the project it just auto-registered"
            );
        }
        await Call(
            "POST",
            "/api/v1/new-code/resolve",
            new
            {
                project_key = autoKey + "-no-profile",
                repository_url = "https://dev.azure.com/org/project/_git/auto-register-repo",
                branch = "main",
            },
            registerCi,
            422
        );
        var otherKey = key + "-other";
        await Call(
            "POST",
            "/api/v1/projects",
            new
            {
                key = otherKey,
                name = otherKey,
                analysis_profile_id = "p0-dotnet",
                analysis_profile_version = "1",
            },
            admin
        );
        var otherToken = await Call(
            "POST",
            "/api/v1/platform/token/generate",
            new
            {
                token_name = otherKey,
                project_keys = new[] { otherKey },
                scopes = new[] { "analysis:read", "analysis:write" },
            },
            admin
        );
        var other = otherToken.GetProperty("token").GetString()!;
        const string sarif =
            """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"fixture"}},"results":[]}]}""";
        var initial = Manifest("main", "sarif", "2.1.0", sarif);
        var first = await Call("POST", "/api/v1/ingestions", initial, ci, 201);
        var id = first.GetProperty("ingestion_id").GetString()!;
        var analysis = first.GetProperty("analysis_id").GetString()!;
        var repeated = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Call("POST", "/api/v1/ingestions", initial, ci))
        );
        Assert.True(
            repeated.All(x => x.GetProperty("ingestion_id").GetString() == id),
            "concurrent identical manifests have one receipt"
        );
        await Call(
            "POST",
            "/api/v1/ingestions",
            initial with
            {
                SourceRoot = "/different-checkout",
            },
            ci,
            409
        );
        await Call("POST", $"/api/v1/ingestions/{id}/finalize", token: ci, expected: 409);
        await Call("GET", $"/api/v1/ingestions/{id}", token: other, expected: 403);
        await Call("POST", $"/api/v1/ingestions/{id}/finalize", token: other, expected: 403);
        await Upload(id, "main", sarif, other, 403);
        await Call("POST", "/api/v1/ingestions", initial, other, 403);
        await Upload(id, "main", sarif.Replace("fixture", "invalid"), ci, 422);
        var pending = await Call("GET", $"/api/v1/ingestions/{id}", token: ci);
        Assert.True(
            pending.GetProperty("reports")[0].GetProperty("state").GetString() == "expected",
            "hash mismatch is not acknowledged"
        );
        await using (var db = Db())
        {
            var stored = await db.Set<IngestionReport>().SingleAsync(x => x.IngestionId == id, ct);
            Assert.True(
                stored.BlobName.Length == 0 && stored.State == "expected",
                "hash mismatch has no committed blob reference"
            );
        }
        await Upload(id, "main", sarif, ci);
        await Call("GET", $"/api/v1/ingestions/{id}/reports/main", token: other, expected: 403);
        using (
            var downloaded = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/ingestions/{id}/reports/main"
            )
        )
        {
            downloaded.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ci);
            using var response = await client.SendAsync(downloaded, ct);
            Assert.True(
                response.IsSuccessStatusCode
                    && await response.Content.ReadAsStringAsync(ct) == sarif,
                "original report download"
            );
        }
        await Call("POST", $"/api/v1/ingestions/{id}/finalize", token: ci, expected: 202);
        var complete = await Terminal(id);
        Assert.True(
            complete.GetProperty("state").GetString() == "completed"
                && complete.GetProperty("quality_gate").GetString() == "pass",
            "empty classified SARIF yields measured zero and pass"
        );
        const string lcov = "SF:src/a.cs\nDA:1,1\nDA:2,0\nend_of_record\n";
        var contribution = Manifest("coverage", "lcov", "1", lcov);
        var second = await Call("POST", "/api/v1/ingestions", contribution, ci, 201);
        var revisionId = second.GetProperty("ingestion_id").GetString()!;
        Assert.True(
            second.GetProperty("analysis_id").GetString() == analysis
                && second.GetProperty("revision").GetInt32() == 2
                && second.GetProperty("reports").GetArrayLength() == 2,
            "complementary report creates a merged revision on the same analysis"
        );
        await Upload(revisionId, "coverage", lcov, ci);
        await Call("POST", $"/api/v1/ingestions/{revisionId}/finalize", token: ci, expected: 202);
        var updated = await Terminal(revisionId);
        Assert.True(
            updated.GetProperty("quality_gate").GetString() == "pass"
                && updated
                    .GetProperty("gate_snapshot")
                    .GetProperty("metrics")
                    .GetProperty("coverage")
                    .GetDouble() == 50,
            "complementary revision combines normalized data"
        );
        var historical = await Call("GET", $"/api/v1/ingestions/{id}", token: ci);
        Assert.True(
            historical.GetProperty("quality_gate").GetString() == "pass"
                && historical.GetProperty("reports").GetArrayLength() == 1,
            "old revision verdict and report set preserved"
        );
        await using (var db = Db())
            Assert.True(
                await db.Set<AnalysisRecord>().CountAsync(x => x.AnalysisId == analysis, ct) == 1,
                "one analysis for both contributions"
            );
        const string correctedSarif =
            """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"fixture"}},"results":[{"ruleId":"R1","level":"error","message":{"text":"Corrected finding"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":1}}}]}]}]}""";
        var correction = await Call(
            "POST",
            "/api/v1/ingestions",
            Manifest("main", "sarif", "2.1.0", correctedSarif),
            ci,
            201
        );
        var correctedId = correction.GetProperty("ingestion_id").GetString()!;
        Assert.True(
            correction.GetProperty("analysis_id").GetString() == analysis
                && correction.GetProperty("revision").GetInt32() == 3,
            "changed content creates a traceable revision"
        );
        await Upload(correctedId, "main", correctedSarif, ci);
        await Call("POST", $"/api/v1/ingestions/{correctedId}/finalize", token: ci, expected: 202);
        var corrected = await Terminal(correctedId);
        Assert.True(
            corrected.GetProperty("quality_gate").GetString() == "fail",
            "corrected finding updates the current verdict"
        );
        historical = await Call("GET", $"/api/v1/ingestions/{id}", token: ci);
        Assert.True(
            historical.GetProperty("quality_gate").GetString() == "pass",
            "changed verdict preserves prior revision verdict"
        );
        var corrupt = Manifest("broken", "sarif", "2.1.0", "{invalid") with
        {
            BuildId = key + "-invalid",
        };
        var rejected = await Call("POST", "/api/v1/ingestions", corrupt, ci, 201);
        var badId = rejected.GetProperty("ingestion_id").GetString()!;
        var badAnalysisId = rejected.GetProperty("analysis_id").GetString()!;
        await Upload(badId, "broken", "{invalid", ci);
        await Call("POST", $"/api/v1/ingestions/{badId}/finalize", token: ci, expected: 202);
        var failed = await Terminal(badId);
        Assert.True(
            failed.GetProperty("state").GetString() == "failed"
                && failed.GetProperty("technical_state").GetString() == "failed"
                && failed.GetProperty("quality_gate").GetString() != "pass",
            "corrupt SARIF reaches terminal failure without success"
        );
        await using (var db = Db())
            Assert.True(
                await db.Set<DurableJob>()
                    .AnyAsync(
                        x =>
                            x.ResourceId.StartsWith(badAnalysisId + ":")
                            && x.Kind == "pr-decoration",
                        ct
                    ),
                "terminal failure persists a PR decoration outbox event"
            );
        var oversized = initial with
        {
            BuildId = key + "-oversize",
            Reports = [initial.Reports[0] with { SizeBytes = 50L * 1024 * 1024 + 1 }],
        };
        await Call("POST", "/api/v1/ingestions", oversized, ci, 422);
        foreach (var missingState in new[] { "absent", "failed" })
        {
            var missing = initial with
            {
                BuildId = key + "-" + missingState,
                Reports =
                [
                    initial.Reports[0] with
                    {
                        State = missingState,
                        SizeBytes = 0,
                        Sha256 = "",
                    },
                ],
            };
            var receipt = await Call("POST", "/api/v1/ingestions", missing, ci, 201);
            var missingId = receipt.GetProperty("ingestion_id").GetString()!;
            await Call(
                "POST",
                $"/api/v1/ingestions/{missingId}/finalize",
                token: ci,
                expected: 202
            );
            Assert.True(
                (await Terminal(missingId)).GetProperty("technical_state").GetString()
                    == "incomplete",
                "missing producer cannot complete technically"
            );
            var result = await Call(
                "GET",
                "/api/v1/analyses/" + receipt.GetProperty("analysis_id").GetString(),
                token: ci
            );
            Assert.True(
                result
                    .GetProperty("measurements")
                    .GetProperty("bugs")
                    .GetProperty("state")
                    .GetString() == missingState,
                "measurement preserves " + missingState
            );
        }
        const string cleanSbom =
            """{"bomFormat":"CycloneDX","specVersion":"1.6","version":1,"components":[],"vulnerabilities":[]}""";
        var clean = await Call(
            "POST",
            "/api/v1/ingestions",
            Manifest("sbom", "cyclonedx", "1.6", cleanSbom) with
            {
                BuildId = key + "-clean-sbom",
            },
            ci,
            201
        );
        var cleanId = clean.GetProperty("ingestion_id").GetString()!;
        await Upload(cleanId, "sbom", cleanSbom, ci);
        await Call("POST", $"/api/v1/ingestions/{cleanId}/finalize", token: ci, expected: 202);
        Assert.True(
            (await Terminal(cleanId))
                .GetProperty("gate_snapshot")
                .GetProperty("metrics")
                .GetProperty("dependency_vulnerabilities")
                .GetInt32() == 0,
            "explicit clean SBOM assessment is zero"
        );
        var baselineManifest = Manifest("main", "sarif", "2.1.0", correctedSarif) with
        {
            BuildId = key + "-baseline",
            CommitSha = "bbbbbb",
            ProjectVersion = "1",
        };
        var baselineReceipt = await Call("POST", "/api/v1/ingestions", baselineManifest, ci, 201);
        var baselineId = baselineReceipt.GetProperty("ingestion_id").GetString()!;
        await Upload(baselineId, "main", correctedSarif, ci);
        await Call("POST", $"/api/v1/ingestions/{baselineId}/finalize", token: ci, expected: 202);
        await Terminal(baselineId);
        await using (var db = Db())
        {
            var baselineRow = await db.Set<IngestionRecord>().SingleAsync(x => x.Id == baselineId, ct);
            baselineRow.TechnicalState = "incomplete";
            await db.SaveChangesAsync(ct);
        }
        const string changedSarif =
            """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"fixture"}},"results":[{"ruleId":"R1","level":"error","message":{"text":"Corrected finding"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":2}}}]},{"ruleId":"R1","level":"error","message":{"text":"New finding"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":3}}}]}]}]}""";
        var changedManifest = Manifest("main", "sarif", "2.1.0", changedSarif) with
        {
            BuildId = key + "-changed",
            CommitSha = "cccccc",
            ProjectVersion = "2",
            TargetSha = "bbbbbb",
            GitDiff = new GitDiff
            {
                State = "available",
                BaseSha = "bbbbbb",
                HeadSha = "cccccc",
                Files = [new GitFileDiff("src/a.cs", null, [3], new() { ["1"] = 2 })],
            },
        };
        var changedReceipt = await Call("POST", "/api/v1/ingestions", changedManifest, ci, 201);
        var changedId = changedReceipt.GetProperty("ingestion_id").GetString()!;
        await Upload(changedId, "main", changedSarif, ci);
        await Call("POST", $"/api/v1/ingestions/{changedId}/finalize", token: ci, expected: 202);
        await Terminal(changedId);
        var comparison = await Call(
            "GET",
            "/api/v1/insights/analyses/"
                + changedReceipt.GetProperty("analysis_id").GetString()
                + "/comparison",
            token: ci
        );
        Assert.True(
            comparison.GetProperty("state").GetString() == "available"
                && comparison.GetProperty("new_count").GetInt32() == 1
                && comparison.GetProperty("persistent_count").GetInt32() == 1
                && comparison.GetProperty("resolved_count").GetInt32() == 0,
            "completed baseline with compatible measured producers remains eligible for reproducible New Code"
        );
        var newCodeProfile = profile with
        {
            Version = "2",
            Conditions = [new("bugs", "max", 0, "new_code")],
            SecurityReview = new(false, 100, "new_code"),
            Rules = [.. profile.Rules, new("fixture", "HS", "security_hotspot")],
        };
        await Call("POST", "/api/v1/profiles", newCodeProfile, admin, 201);
        await Call(
            "PUT",
            $"/api/v1/profiles/projects/{key}",
            new ProfileAssignment(key, "2"),
            admin
        );
        var evidenceSarif = correctedSarif.Replace(
            "\"name\":\"fixture\"",
            "\"name\":\"fixture\",\"version\":\"1\",\"rules\":[{\"id\":\"R1\",\"helpUri\":\"https://docs.example.invalid/rules/R1\"}]"
        );
        var scopedBaseline = baselineManifest with
        {
            ProfileVersion = "2",
            BuildId = key + "-p3-baseline",
            CommitSha = new string('b', 40),
            RepositoryUrl = "https://dev.azure.com/checks/project/_git/fixture",
            Reports = Manifest("main", "sarif", "2.1.0", evidenceSarif).Reports,
        };
        var p3Baseline = await Publish(scopedBaseline, evidenceSarif);
        var evidenceAnalysis = p3Baseline.GetProperty("analysis_id").GetString();
        var evidenceRows = await Call(
            "GET",
            $"/api/v1/insights/analyses/{evidenceAnalysis}/findings",
            token: ci
        );
        Assert.True(
            evidenceRows[0].GetProperty("evidence").GetProperty("documentation_url").GetString()
                == "https://docs.example.invalid/rules/R1"
                && evidenceRows[0]
                    .GetProperty("source_url")
                    .GetString()!
                    .Contains("version=GC" + new string('b', 40)),
            "persisted SARIF documentation and source link use the exact analysis commit"
        );
        await Call(
            "GET",
            $"/api/v1/insights/analyses/{evidenceAnalysis}/findings",
            token: other,
            expected: 403
        );
        Assert.True(
            p3Baseline.GetProperty("quality_gate").GetString() == "unknown"
                && p3Baseline.GetProperty("technical_state").GetString() == "complete",
            "new-code baseline without its own comparison is technically complete but never passes"
        );
        var scopedChange = changedManifest with
        {
            ProfileVersion = "2",
            RepositoryUrl = scopedBaseline.RepositoryUrl,
            BuildId = key + "-p3-change",
            CommitSha = new string('c', 40),
            TargetSha = new string('b', 40),
            GitDiff = changedManifest.GitDiff with
            {
                BaseSha = new string('b', 40),
                HeadSha = new string('c', 40),
            },
        };
        var p3Failed = await Publish(scopedChange, changedSarif);
        Assert.True(
            p3Failed.GetProperty("quality_gate").GetString() == "fail",
            "new defect fails the scoped gate"
        );
        var scopedCondition = p3Failed.GetProperty("gate_snapshot").GetProperty("conditions")[0];
        Assert.True(
            scopedCondition.GetProperty("scope").GetString() == "new_code"
                && scopedCondition.GetProperty("value").GetInt32() == 1
                && scopedCondition.GetProperty("base_sha").GetString() == new string('b', 40),
            "persisted gate carries scoped value and frozen baseline"
        );
        var fixedSarif = correctedSarif.Replace("\"startLine\":1", "\"startLine\":2");
        var p3Fixed = await Publish(
            scopedChange with
            {
                BuildId = key + "-p3-fixed",
                CommitSha = new string('d', 40),
                Reports = Manifest("main", "sarif", "2.1.0", fixedSarif).Reports,
                GitDiff = scopedChange.GitDiff with { HeadSha = new string('d', 40) },
            },
            fixedSarif
        );
        Assert.True(
            p3Fixed.GetProperty("quality_gate").GetString() == "pass"
                && p3Fixed
                    .GetProperty("gate_snapshot")
                    .GetProperty("metrics")
                    .GetProperty("bugs")
                    .GetInt32() == 1,
            "fixed PR passes despite one persistent historical bug"
        );
        var p3Missing = await Publish(
            scopedChange with
            {
                BuildId = key + "-p3-missing",
                NewCodeReferenceBranch = "refs/heads/missing",
                TargetSha = new string('e', 40),
                GitDiff = scopedChange.GitDiff with { BaseSha = new string('e', 40) },
            },
            changedSarif
        );
        Assert.True(
            p3Missing.GetProperty("quality_gate").GetString() == "unknown",
            "absent baseline cannot pass scoped gate"
        );
        var p3Historical = await Call(
            "GET",
            "/api/v1/ingestions/" + p3Failed.GetProperty("ingestion_id").GetString(),
            token: ci
        );
        Assert.True(
            p3Historical.GetProperty("quality_gate").GetString() == "fail",
            "fix never rewrites failing historical gate"
        );
        await Call(
            "GET",
            "/api/v1/insights/analyses/"
                + p3Fixed.GetProperty("analysis_id").GetString()
                + "/comparison",
            token: other,
            expected: 403
        );
        Console.WriteLine(
            "P3 scoped gate HTTP checks passed: new defect, correction with historical debt, missing baseline, immutable verdict, project isolation."
        );
        var measuredProfile = profile with
        {
            Version = "4",
            RequiredMetrics = ["bugs", "branch_coverage", "duplicate_blocks", "code_lines"],
            Conditions =
            [
                new("bugs", "max", 10),
                new("branch_coverage", "min", 50),
                new("duplicate_blocks", "max", 1),
                new("code_lines", "min", 12),
            ],
            RuleCatalog =
            [
                new(
                    "fixture",
                    "1",
                    "R1",
                    "csharp",
                    "bug",
                    JsonSerializer.SerializeToElement(new { }),
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview",
                    Remediation: new(
                        "constant",
                        60,
                        "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview"
                    )
                ),
            ],
            AnalyzerConfigurations =
            [
                new(
                    "fixture",
                    "1",
                    "editorconfig",
                    ".editorconfig",
                    "root = true\n[*.cs]\ndotnet_diagnostic.R1.severity = warning\n"
                ),
            ],
        };
        await Call("POST", "/api/v1/profiles", measuredProfile, admin, 201);
        await Call(
            "PUT",
            $"/api/v1/profiles/projects/{key}",
            new ProfileAssignment(key, "4"),
            admin
        );
        var exported = ExecutableProfiles.Export(measuredProfile);
        var codeReports = new Dictionary<string, (string Format, string Version, string Content)>
        {
            ["main"] = ("sarif", "2.1.0", correctedSarif),
            ["coverage"] = (
                "lcov",
                "1",
                "SF:src/a.cs\nDA:1,1\nDA:2,0\nBRDA:1,0,0,1\nBRDA:1,0,1,0\nend_of_record\n"
            ),
            ["duplicates"] = (
                "jscpd",
                "1",
                """{"duplicates":[{"format":"csharp","lines":3,"tokens":20,"firstFile":{"name":"src/a.cs","start":1,"end":3},"secondFile":{"name":"src/b.cs","start":1,"end":3}}]}"""
            ),
            ["duplicates-two"] = (
                "jscpd",
                "1",
                """{"duplicates":[{"format":"csharp","lines":3,"tokens":20,"firstFile":{"name":"src/a.cs","start":1,"end":3},"secondFile":{"name":"src/b.cs","start":1,"end":3}}]}"""
            ),
            ["size"] = (
                "cloc",
                "1",
                """{"header":{"cloc_version":"1"},"src/a.cs":{"language":"C#","code":12,"comment":2,"blank":1},"src/b.cs":{"language":"C#","code":12,"comment":0,"blank":0},"SUM":{"code":24}}"""
            ),
        };
        var codeManifest = initial with
        {
            ProfileVersion = "4",
            BuildId = key + "-native-metrics",
            CommitSha = new string('f', 40),
            RepositoryUrl = "https://dev.azure.com/checks/project/_git/fixture",
            RepositoryFiles = ["src/a.cs", "src/b.cs"],
            EffectiveConfigurations = exported
                .Files.Select(f => new EffectiveConfiguration(
                    f.Tool,
                    f.ToolVersion,
                    f.Path,
                    f.Sha256
                ))
                .ToArray(),
            Reports = codeReports
                .Select(r =>
                    Manifest(r.Key, r.Value.Format, r.Value.Version, r.Value.Content).Reports[0]
                )
                .ToList(),
        };
        var codeResult = await PublishCode(codeManifest);
        Assert.True(
            codeResult.GetProperty("quality_gate").GetString() == "pass",
            "global gate consumes real branch, duplicate and size measurements"
        );
        var codeSnapshot = codeResult.GetProperty("gate_snapshot");
        Assert.True(
            codeSnapshot.GetProperty("metrics").GetProperty("branch_coverage").GetDouble() == 50
                && codeSnapshot.GetProperty("metrics").GetProperty("duplicate_blocks").GetInt32()
                    == 1
                && codeSnapshot.GetProperty("metrics").GetProperty("code_lines").GetInt32() == 24
                && codeSnapshot.GetProperty("configuration").GetProperty("state").GetString()
                    == "matched",
            "native metrics and effective configuration are persisted exactly"
        );
        var codeAnalysis = codeResult.GetProperty("analysis_id").GetString()!;
        var codeQuality = await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/code-quality",
            token: ci
        );
        Assert.True(
            codeQuality.GetProperty("coverage").GetProperty("state").GetString() == "measured"
                && codeQuality.GetProperty("coverage").GetProperty("covered_lines").GetInt64() == 1
                && codeQuality.GetProperty("coverage").GetProperty("uncovered_lines").GetInt64() == 1
                && codeQuality.GetProperty("coverage").GetProperty("percentage").GetDouble() == 50
                && codeQuality.GetProperty("duplication").GetProperty("state").GetString() == "measured"
                && codeQuality.GetProperty("duplication").GetProperty("duplicate_blocks").GetInt32() == 1
                && codeQuality.GetProperty("duplication").GetProperty("duplicated_lines").GetInt64() == 6,
            "code quality summary distinguishes measured native evidence"
        );
        var coverageFiles = await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/coverage/files?search=a.cs",
            token: ci
        );
        Assert.True(
            coverageFiles.GetProperty("state").GetString() == "measured"
                && coverageFiles.GetProperty("files")[0].GetProperty("path").GetString() == "src/a.cs"
                && coverageFiles.GetProperty("files")[0].GetProperty("uncovered_lines").GetInt64() == 1
                && coverageFiles.GetProperty("files")[0].GetProperty("source_url").GetString()!
                    .Contains("version=GC" + new string('f', 40)),
            "coverage files are ranked with commit-pinned safe source links"
        );
        using (var pageRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/insights/analyses/{codeAnalysis}/coverage/lines?path=src%2Fa.cs&covered=all&limit=1"
        ))
        {
            pageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ci);
            using var pageResponse = await client.SendAsync(pageRequest, ct);
            var page = JsonSerializer.Deserialize<JsonElement>(await pageResponse.Content.ReadAsStringAsync(ct));
            var hasCursor = pageResponse.Headers.TryGetValues("X-Next-Cursor", out _);
            Assert.True(
                pageResponse.IsSuccessStatusCode
                    && page.GetProperty("lines")[0].GetProperty("line").GetInt32() == 1
                    && hasCursor
                    && pageResponse.Headers.Contains("Link"),
                "coverage lines expose opaque continuation headers"
            );
            var next = pageResponse.Headers.GetValues("X-Next-Cursor").Single();
            var secondPage = await Call(
                "GET",
                $"/api/v1/insights/analyses/{codeAnalysis}/coverage/lines?path=src%2Fa.cs&covered=all&limit=1&cursor={Uri.EscapeDataString(next)}",
                token: ci
            );
            Assert.True(
                secondPage.GetProperty("lines")[0].GetProperty("line").GetInt32() == 2,
                "coverage continuation resumes after the last executable line"
            );
        }
        var duplicates = await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/duplications?search=csharp",
            token: ci
        );
        Assert.True(
            duplicates.GetProperty("state").GetString() == "measured"
                && duplicates.GetProperty("duplications")[0].GetProperty("report_id").GetString() == "duplicates"
                && duplicates.GetProperty("duplications").GetArrayLength() == 2
                && duplicates.GetProperty("duplications")[1].GetProperty("report_id").GetString() == "duplicates-two"
                && duplicates.GetProperty("duplications")[0].GetProperty("tool_version").GetString() == "1"
                && duplicates.GetProperty("duplications")[0].GetProperty("first").GetProperty("path").GetString() == "src/a.cs"
                && duplicates.GetProperty("duplications")[0].GetProperty("first").GetProperty("source_url").GetString()!
                    .Contains("lineEnd=4"),
            "duplication details preserve producer provenance and both safe locations"
        );
        const string zeroDuplicates = "{\"duplicates\":[]}";
        var pendingDuplicate = (Format: "jscpd", Version: "1", Content: zeroDuplicates);
        var pendingDescriptor = Manifest(
            "duplicates",
            pendingDuplicate.Format,
            pendingDuplicate.Version,
            pendingDuplicate.Content
        ).Reports[0];
        var pendingRevision = await Call(
            "POST",
            "/api/v1/ingestions",
            codeManifest with { Reports = [pendingDescriptor] },
            ci,
            201
        );
        Assert.True(
            pendingRevision.GetProperty("analysis_id").GetString() == codeAnalysis,
            "a complementary contribution keeps the analysis identity"
        );
        var whilePending = await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/code-quality",
            token: ci
        );
        Assert.True(
            whilePending.GetProperty("duplication").GetProperty("duplicate_blocks").GetInt32() == 1,
            "a pending later revision never hides the latest completed native evidence"
        );
        await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/code-quality",
            token: other,
            expected: 403
        );
        await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/coverage/lines?path=..%2Fsecret",
            token: ci,
            expected: 422
        );
        await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/coverage/lines?path=src%2Fa.cs&covered=maybe",
            token: ci,
            expected: 422
        );
        await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/duplications?cursor=invalid",
            token: ci,
            expected: 422
        );
        var zeroReports = new Dictionary<string, (string Format, string Version, string Content)>(codeReports)
        {
            ["coverage"] = (
                "lcov",
                "1",
                "SF:src/a.cs\nDA:1,1\nDA:2,1\nBRDA:1,0,0,1\nBRDA:1,0,1,1\nend_of_record\n"
            ),
            ["duplicates"] = ("jscpd", "1", zeroDuplicates),
            ["duplicates-two"] = ("jscpd", "1", zeroDuplicates),
        };
        var zeroManifest = codeManifest with
        {
            BuildId = key + "-native-zero-duplicates",
            CommitSha = new string('e', 40),
            Reports = zeroReports
                .Select(r => Manifest(r.Key, r.Value.Format, r.Value.Version, r.Value.Content).Reports[0])
                .ToList(),
        };
        var zeroResult = await PublishCode(zeroManifest, zeroReports);
        var zeroQuality = await Call(
            "GET",
            $"/api/v1/insights/analyses/{zeroResult.GetProperty("analysis_id").GetString()}/code-quality",
            token: ci
        );
        Assert.True(
            zeroQuality.GetProperty("coverage").GetProperty("state").GetString() == "measured"
                && zeroQuality.GetProperty("coverage").GetProperty("percentage").GetDouble() == 100
                && zeroQuality.GetProperty("coverage").GetProperty("uncovered_lines").GetInt64() == 0
                && zeroQuality.GetProperty("duplication").GetProperty("state").GetString() == "measured"
                && zeroQuality.GetProperty("duplication").GetProperty("duplicate_blocks").GetInt32() == 0
                && zeroQuality.GetProperty("duplication").GetProperty("duplicated_lines").GetInt64() == 0,
            "complete coverage and measured zero duplicated blocks remain real measured results"
        );
        var notMeasured = await Call(
            "GET",
            $"/api/v1/insights/analyses/{analysis}/code-quality",
            token: ci
        );
        Assert.True(
            notMeasured.GetProperty("duplication").GetProperty("state").GetString() == "not_measured"
                && notMeasured.GetProperty("duplication").GetProperty("reason").GetString() == "report_not_measured",
            "an ingestion without jscpd stays explicitly not measured"
        );
        var direct = await Call(
            "POST",
            "/api/v1/analyses",
            new AnalysisRequest
            {
                ProjectKey = key,
                CommitSha = new string('d', 40),
                Branch = "refs/heads/direct",
                CiProvider = "local",
                ProfileId = key,
                ProfileVersion = "4",
                FormulaVersion = "1",
                RepositoryUrl = "https://dev.azure.com/checks/project/_git/fixture",
            },
            ci
        );
        var directQuality = await Call(
            "GET",
            $"/api/v1/insights/analyses/{direct.GetProperty("analysis_id").GetString()}/code-quality",
            token: ci
        );
        Assert.True(
            directQuality.GetProperty("coverage").GetProperty("reason").GetString() == "direct_ingestion"
                && directQuality.GetProperty("duplication").GetProperty("reason").GetString() == "direct_ingestion",
            "direct analysis metrics never pretend to contain native report evidence"
        );
        await using (var db = Db())
        {
            var analysisRow = await db.Set<AnalysisRecord>().SingleAsync(x => x.AnalysisId == codeAnalysis, ct);
            analysisRow.DetailsPurged = true;
            await db.SaveChangesAsync(ct);
        }
        var analysisPurged = await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/code-quality",
            token: ci
        );
        Assert.True(
            analysisPurged.GetProperty("coverage").GetProperty("reason").GetString() == "details_purged",
            "analysis purge marker wins during the retention consistency window"
        );
        await using (var db = Db())
        {
            var analysisRow = await db.Set<AnalysisRecord>().SingleAsync(x => x.AnalysisId == codeAnalysis, ct);
            var ingestionRow = await db.Set<IngestionRecord>()
                .SingleAsync(x => x.AnalysisId == codeAnalysis && x.CompletedAt != null, ct);
            analysisRow.DetailsPurged = false;
            ingestionRow.DetailsPurged = true;
            await db.SaveChangesAsync(ct);
        }
        var ingestionPurged = await Call(
            "GET",
            $"/api/v1/insights/analyses/{codeAnalysis}/code-quality",
            token: ci
        );
        Assert.True(
            ingestionPurged.GetProperty("duplication").GetProperty("reason").GetString() == "details_purged",
            "ingestion purge marker independently prevents stale native evidence"
        );
        Assert.True(
            codeSnapshot.GetProperty("remediation").GetProperty("estimated_seconds").GetInt32()
                == 60,
            "documented remediation model stays an explicit estimate"
        );
        var drift = await PublishCode(
            codeManifest with
            {
                BuildId = key + "-configuration-drift",
                EffectiveConfigurations =
                [
                    codeManifest.EffectiveConfigurations[0] with
                    {
                        Sha256 = new string('0', 64),
                    },
                ],
            }
        );
        Assert.True(
            drift.GetProperty("quality_gate").GetString() == "unknown"
                && drift
                    .GetProperty("gate_snapshot")
                    .GetProperty("configuration")
                    .GetProperty("state")
                    .GetString() == "drift",
            "configuration drift cannot satisfy a passing measured gate"
        );
        Console.WriteLine(
            "P3 native metrics/configuration HTTP checks passed: LCOV branches, jscpd blocks, cloc files, remediation estimate and drift."
        );
        await LeaseChecks(connection, key, ct);
        Console.WriteLine(
            $"P1 real HTTP/Blob/PostgreSQL checks passed: {key} (idempotence, revisions, hash, corruption, sizes, cross-project access, leases)."
        );

        AppDbContext Db() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        async Task<JsonElement> PublishCode(
            IngestionManifest manifest,
            IReadOnlyDictionary<string, (string Format, string Version, string Content)>? reports = null
        )
        {
            reports ??= codeReports;
            var receipt = await Call("POST", "/api/v1/ingestions", manifest, ci, 201);
            var ingestionId = receipt.GetProperty("ingestion_id").GetString()!;
            foreach (var report in reports)
                await Upload(ingestionId, report.Key, report.Value.Content, ci);
            await Call(
                "POST",
                $"/api/v1/ingestions/{ingestionId}/finalize",
                token: ci,
                expected: 202
            );
            return await Terminal(ingestionId);
        }
        async Task<JsonElement> Publish(IngestionManifest manifest, string content)
        {
            var receipt = await Call("POST", "/api/v1/ingestions", manifest, ci, 201);
            var ingestionId = receipt.GetProperty("ingestion_id").GetString()!;
            await Upload(ingestionId, "main", content, ci);
            await Call(
                "POST",
                $"/api/v1/ingestions/{ingestionId}/finalize",
                token: ci,
                expected: 202
            );
            return await Terminal(ingestionId);
        }
        IngestionManifest Manifest(
            string reportId,
            string format,
            string version,
            string content
        ) =>
            new()
            {
                ProjectKey = key,
                RepositoryUrl = "https://example.invalid/" + key,
                CiProvider = "local",
                BuildId = key,
                CommitSha = "abcdef123456",
                Branch = "refs/heads/checks",
                ProfileId = key,
                ProfileVersion = "1",
                Reports =
                [
                    new(
                        reportId,
                        format,
                        version,
                        "fixture",
                        "1",
                        "measured",
                        Encoding.UTF8.GetByteCount(content),
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                    ),
                ],
            };
        async Task<JsonElement> Call(
            string method,
            string path,
            object? body = null,
            string? token = null,
            int expected = 200
        )
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: ApiJson.Options);
            using var response = await client.SendAsync(request, ct);
            Assert.True(
                (int)response.StatusCode == expected,
                $"{method} {path}: expected {expected}, got {(int)response.StatusCode}"
            );
            var raw = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(raw)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(raw);
        }
        async Task Upload(
            string ingestion,
            string report,
            string content,
            string token,
            int expected = 200
        )
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/ingestions/{ingestion}/reports/{report}"
            )
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, ct);
            Assert.True(
                (int)response.StatusCode == expected,
                $"Upload expected {expected}, got {(int)response.StatusCode}"
            );
        }
        async Task<JsonElement> Terminal(string ingestion)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var receipt = await Call("GET", $"/api/v1/ingestions/{ingestion}", token: ci);
                if (receipt.GetProperty("state").GetString() is "completed" or "failed")
                    return receipt;
                await Task.Delay(250, ct);
            }
            throw new InvalidOperationException(
                "Ingestion did not reach terminal state in 20 seconds."
            );
        }
    }

    private static async Task LeaseChecks(string connection, string prefix, CancellationToken ct)
    {
        // The API under test runs its IngestionWorker on the shared queue and would claim these jobs
        // between two assertions: claim ordering is checked on a private database no worker polls.
        var isolated = new NpgsqlConnectionStringBuilder(connection)
        {
            Database = "codeinsights_lease_" + Guid.NewGuid().ToString("N")[..12],
            Pooling = false,
        };
        await ExecuteAsync($"CREATE DATABASE \"{isolated.Database}\"");
        try
        {
            AppDbContext Db() =>
                new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options);
            await using var setup = Db();
            await setup.Database.MigrateAsync(ct);
            var ids = Enumerable.Range(0, 6).Select(x => prefix + "-lease-" + x).ToArray();
            setup.AddRange(
                ids.Select(id => new DurableJob
                {
                    Id = id,
                    ResourceId = id,
                    Kind = "check-p1",
                    AvailableAt = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                })
            );
            await setup.SaveChangesAsync(ct);
            await using var firstContext = Db();
            await using var secondContext = Db();
            var options = new IngestionOptions();
            var claims = await Task.WhenAll(
                IngestionWorker.ClaimAsync(firstContext, options, ct),
                IngestionWorker.ClaimAsync(secondContext, options, ct)
            );
            Assert.True(
                claims.All(x => x is not null && ids.Contains(x.Id))
                    && claims[0]!.Id != claims[1]!.Id
                    && claims[0]!.LeaseOwner != claims[1]!.LeaseOwner,
                "concurrent claims reserve different jobs and owners"
            );
            var original = claims[0]!;
            await setup
                .Set<DurableJob>()
                .Where(x => x.Id == original.Id)
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(x => x.LeaseUntil, DateTime.UtcNow.AddSeconds(-1))
                            .SetProperty(
                                x => x.AvailableAt,
                                new DateTime(1800, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            ),
                    ct
                );
            await using var recovery = Db();
            var recovered = await IngestionWorker.ClaimAsync(recovery, options, ct);
            Assert.True(
                recovered?.Id == original.Id
                    && recovered.LeaseOwner != original.LeaseOwner
                    && recovered.Attempts == original.Attempts + 1,
                "expired lease is reclaimed with a new owner and incremented attempt"
            );
        }
        finally
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{isolated.Database}\" WITH (FORCE)");
        }

        async Task ExecuteAsync(string sql)
        {
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, admin);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

}
