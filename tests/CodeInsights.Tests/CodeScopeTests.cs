using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class CodeScopeTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var scope = new CodeScopeDefinition
        {
            Version = "1",
            Component = "api",
            RepositoryUrl = "https://dev.azure.com/org/project/_git/repo",
            IncludedPaths = ["src/"],
            ExcludedPaths = ["src/private/"],
            TestPaths = ["src/tests/"],
            GeneratedPaths = ["src/generated/"],
            ThirdPartyPaths = ["src/vendor/"],
        };
        CodeScopes.Validate(scope);
        Assert.True(
            CodeScopes.IsSource(scope, "src/a.cs")
                && !CodeScopes.Includes(scope, "src/private/key.cs")
                && !CodeScopes.Includes(scope, "src-extra/a.cs"),
            "component prefixes enforce directory boundaries"
        );
        Assert.True(
            CodeScopes.Kind(scope, "src/tests/a.cs") == "test"
                && CodeScopes.Kind(scope, "src/generated/a.cs") == "generated"
                && CodeScopes.Kind(scope, "src/vendor/a.cs") == "third_party",
            "source kinds remain distinct"
        );
        Assert.True(
            !CodeScopes.IsSource(scope, "src/tests/a.cs"),
            "tests do not contribute to source-only metrics"
        );
        Invalid(() => CodeScopes.Validate(scope with { IncludedPaths = ["../secret"] }), 422);
        Invalid(
            () =>
                CodeScopes.Validate(
                    scope with
                    {
                        RepositoryUrl = "https://user:password@example.org/repo",
                    }
                ),
            422
        );
        Invalid(
            () =>
                CodeScopes.Validate(
                    scope with
                    {
                        ReferenceKind = "analysis",
                        ReferenceAnalysisId = null,
                    }
                ),
            422
        );
        var manifest = new IngestionManifest
        {
            RepositoryUrl = scope.RepositoryUrl,
            CodeScopeVersion = scope.Version,
            CommitSha = new string('b', 40),
            TargetSha = new string('a', 40),
            RepositoryFiles = ["src/a.cs"],
            GitDiff = new()
            {
                State = "available",
                BaseSha = new string('a', 40),
                HeadSha = new string('b', 40),
            },
        };
        var snapshot = new CodeScopeSnapshot(scope, manifest.TargetSha, "baseline", null);
        CodeScopes.ValidatePublication(manifest, snapshot);
        Invalid(
            () =>
                CodeScopes.ValidatePublication(
                    manifest with
                    {
                        CodeScopeVersion = null!,
                    },
                    new(null, null, null, null)
                ),
            422
        );
        Invalid(
            () =>
                CodeScopes.ValidatePublication(
                    manifest with
                    {
                        RepositoryFiles = ["src/a.cs", "src/a.cs"],
                    },
                    snapshot
                ),
            422
        );
        Invalid(
            () =>
                CodeScopes.ValidatePublication(
                    manifest with
                    {
                        RepositoryFiles = ["../secret"],
                    },
                    snapshot
                ),
            422
        );
        Invalid(
            () =>
                CodeScopes.ValidatePublication(manifest with { CodeScopeVersion = "2" }, snapshot),
            422
        );
        CodeScopes.ValidatePublication(manifest with { TargetSha = new string('c', 40) }, snapshot);
        Assert.True(
            CodeScopes.CompatibilityKey(scope)
                == CodeScopes.CompatibilityKey(scope with { ReferenceBranch = "refs/heads/release" }),
            "legacy code-scope reference does not override dedicated New Code selection"
        );
        var serialized = JsonSerializer.Serialize(snapshot, ApiJson.Options);
        Assert.True(
            CodeScopes.Read(serialized).Definition?.Version == "1"
                && CodeScopes.Read(serialized).BaselineSha == new string('a', 40),
            "scope snapshot roundtrip retains exact baseline and version"
        );
        var purged = new IngestionRecord
        {
            Manifest = JsonSerializer.Serialize(manifest, ApiJson.Options),
            CodeData = "{\"reports\":[{}]}",
            GateSnapshot = "{\"verdict\":\"fail\"}",
        };
        IngestionMaintenance.PurgeCodeDetails(purged);
        Assert.True(
            purged.CodeData == "{}"
                && JsonSerializer
                    .Deserialize<IngestionManifest>(purged.Manifest, ApiJson.Options)!
                    .RepositoryFiles.Length == 0
                && purged.GateSnapshot.Contains("fail"),
            "retention clears code detail and inventory while preserving verdict"
        );

        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        var url = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_URL");
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine(
                "Code scope checks passed; HTTP persistence checks require isolated database and API."
            );
            return;
        }
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(
            info.Host is "localhost" or "127.0.0.1"
                && info.Database
                    is "codeinsights_checks"
                        or "codeinsights_test"
                        or "codeinsights_p1",
            "isolated local PostgreSQL required"
        );
        Assert.True(
            Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback,
            "loopback API required"
        );
        using var client = new HttpClient { BaseAddress = endpoint };
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options
        );
        var admin = (
            await Call(
                "POST",
                "/api/v1/auth/login",
                new
                {
                    email = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_EMAIL")
                        ?? "admin@codeinsights.local",
                    password = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_PASSWORD")
                        ?? "Isolated-bootstrap-123",
                }
            )
        )
            .GetProperty("token")
            .GetString()!;
        var key = "p3-scope-" + Guid.NewGuid().ToString("N");
        await Call("POST", "/api/v1/projects", new { key, name = "P3 isolated code scope" }, admin);
        var outsider = new PlatformUser
        {
            UserId = Guid.NewGuid().ToString(),
            Email = key + "@checks.local",
            FullName = "Scope outsider",
            HashedPassword = AuthService.HashPassword("Isolated-scope-member-123"),
        };
        db.Add(outsider);
        await db.SaveChangesAsync();
        var other = (
            await Call(
                "POST",
                "/api/v1/auth/login",
                new { email = outsider.Email, password = "Isolated-scope-member-123" }
            )
        )
            .GetProperty("token")
            .GetString()!;
        var executable = new ProfileDefinition
        {
            ProfileId = key,
            Version = "1",
            RequiredTools = ["eslint"],
            Rules = [new("eslint", "no-eval", "vulnerability")],
            RuleCatalog =
            [
                new(
                    "eslint",
                    "9.0.0",
                    "no-eval",
                    "javascript",
                    "vulnerability",
                    JsonSerializer.SerializeToElement(new { }),
                    "https://eslint.org/docs/latest/rules/no-eval"
                ),
            ],
            AnalyzerConfigurations =
            [
                new(
                    "eslint",
                    "9.0.0",
                    "eslint-flat-json",
                    "eslint.config.cjs",
                    "[{\"rules\":{\"no-eval\":\"error\"}}]"
                ),
            ],
        };
        await Call("POST", "/api/v1/profiles", executable, admin, 201);
        await Call("POST", "/api/v1/profiles", executable, admin, 409);
        await Call(
            "PUT",
            $"/api/v1/profiles/projects/{key}",
            new ProfileAssignment(key, "1"),
            admin
        );
        var catalog = await Call(
            "GET",
            $"/api/v1/profiles/{key}/versions/1/catalogue",
            token: admin
        );
        Assert.True(
            catalog.GetArrayLength() == 1
                && catalog[0].GetProperty("rule_id").GetString() == "no-eval",
            "producer catalogue exists without findings"
        );
        var exported = await Call(
            "GET",
            $"/api/v1/profiles/projects/{key}/configuration",
            token: admin
        );
        Assert.True(
            exported.GetProperty("files").GetArrayLength() == 1
                && exported.GetProperty("configuration_hash").GetString()?.Length == 64,
            "native configuration export includes reproducible fingerprint"
        );
        var disabled = await Call(
            "POST",
            $"/api/v1/profiles/projects/{key}/rule-activation",
            new { tool = "eslint", tool_version = "11.0.0", rule_id = "no-eval", enabled = false, expected_profile_id = key, expected_profile_version = "1" },
            admin
        );
        Assert.True(
            disabled.GetProperty("status").GetString() == "disabled"
                && disabled.GetProperty("profile_version").GetString() == "1-rules.1"
                && disabled.GetProperty("tool_version").GetString() == "9.0.0"
                && !disabled.GetProperty("historical_verdicts_changed").GetBoolean(),
            "rule deactivation normalizes provenance and creates a revision without rewriting history"
        );
        exported = await Call("GET", $"/api/v1/profiles/projects/{key}/configuration", token: admin);
        Assert.True(exported.GetProperty("files")[0].GetProperty("content").GetString()!.Contains("\"no-eval\":\"off\""), "project export applies disabled rule");
        var ruleAnalysis = new AnalysisRecord
        {
            AnalysisId = Guid.NewGuid().ToString(), ProjectKey = key, Branch = scope.ReferenceBranch, CommitSha = "abcdef123456",
            CreatedAt = ApiJson.Iso(DateTime.UtcNow), Status = "completed"
        };
        db.Add(ruleAnalysis);
        db.Add(new FindingOccurrence
        {
            AnalysisId = ruleAnalysis.AnalysisId, Tool = "eslint", RuleId = "no-eval", Category = "vulnerability",
            Severity = "high", Message = "Managed rule", Path = "src/a.js", Line = 1
        });
        await db.SaveChangesAsync();
        var disabledRules = await Call("GET", $"/api/v1/insights/rules?project_key={key}&status=disabled&language=javascript", token: admin);
        Assert.True(disabledRules.GetArrayLength() == 1
            && disabledRules[0].GetProperty("tool").GetString() == "eslint"
            && disabledRules[0].GetProperty("tool_version").GetString() == "9.0.0"
            && disabledRules[0].GetProperty("profile_version").GetString() == "1-rules.1",
            "rules insight applies language/status filters and exposes exact profile identity");
        var searchedRules = await Call("GET", $"/api/v1/insights/rules?project_key={key}&status=disabled&language=javascript&search=NO-EV&severity=all", token: admin);
        var wildcardRules = await Call("GET", $"/api/v1/insights/rules?project_key={key}&status=disabled&language=javascript&search=no%25eval", token: admin);
        Assert.True(searchedRules.GetArrayLength() == 1 && wildcardRules.GetArrayLength() == 0,
            "rules search ignores case and treats LIKE wildcards literally");
        var matchedFindings = await Call("GET", $"/api/v1/insights/analyses/{ruleAnalysis.AnalysisId}/findings?rule=NO-EVAL&path=SRC%2FA&severity=high", token: admin);
        var wildcardFindings = await Call("GET", $"/api/v1/insights/analyses/{ruleAnalysis.AnalysisId}/findings?rule=no_eval", token: admin);
        Assert.True(matchedFindings.GetArrayLength() == 1 && wildcardFindings.GetArrayLength() == 0,
            "findings rule/path filters ignore case and treat LIKE wildcards literally");
        await Call(
            "POST",
            $"/api/v1/profiles/projects/{key}/rule-activation",
            new { tool = "eslint", tool_version = "9.0.0", rule_id = "no-eval", enabled = true, expected_profile_id = key, expected_profile_version = "1" },
            admin,
            409
        );
        await Call(
            "POST",
            $"/api/v1/profiles/projects/{key}/rule-activation",
            new { tool = "eslint", tool_version = "12.0.0", rule_id = "no-eval", enabled = true, expected_profile_id = key, expected_profile_version = "1-rules.1" },
            other,
            403
        );
        var enabled = await Call(
            "POST",
            $"/api/v1/profiles/projects/{key}/rule-activation",
            new { tool = "eslint", tool_version = "9.0.0", rule_id = "no-eval", enabled = true, expected_profile_id = key, expected_profile_version = "1-rules.1" },
            admin
        );
        Assert.True(enabled.GetProperty("profile_version").GetString() == "1-rules.2"
            && enabled.GetProperty("tool_version").GetString() == "9.0.0", "rule reactivation normalizes provenance and increments the readable profile revision");
        var enabledRules = await Call("GET", $"/api/v1/insights/rules?project_key={key}&status=enabled&language=javascript", token: admin);
        Assert.True(enabledRules.GetArrayLength() == 1 && enabledRules[0].GetProperty("status").GetString() == "enabled",
            "rules insight reflects the newly assigned profile version");
        exported = await Call("GET", $"/api/v1/profiles/projects/{key}/configuration", token: admin);
        Assert.True(exported.GetProperty("files")[0].GetProperty("content").GetString()!.Contains("\"no-eval\":\"error\""), "rule reactivation restores source configuration");
        await Call(
            "GET",
            $"/api/v1/profiles/projects/{key}/configuration",
            token: other,
            status: 403
        );
        await Call(
            "POST",
            "/api/v1/profiles",
            executable with
            {
                Version = "2",
                Conditions = [new("vulnerabilities", "max", 1)],
            },
            admin,
            201
        );
        await Call("GET", $"/api/v1/profiles/{key}/versions/2/diff/1", token: admin);
        var impact = await Call(
            "GET",
            $"/api/v1/profiles/projects/{key}/impact?profileId={key}&version=2",
            token: admin
        );
        Assert.True(
            !impact.GetProperty("historical_verdicts_changed").GetBoolean(),
            "profile impact does not claim to rewrite history"
        );
        await Call("POST", $"/api/v1/code/projects/{key}/versions", scope, admin, 201);
        await Call("POST", $"/api/v1/code/projects/{key}/versions", scope, admin, 409);
        await Call(
            "PUT",
            $"/api/v1/code/projects/{key}/configuration",
            new { version = "1" },
            admin
        );
        await Call("GET", $"/api/v1/code/projects/{key}/configuration", token: other, status: 403);
        var before = await Call("GET", $"/api/v1/code/projects/{key}/configuration", token: admin);
        Assert.True(
            before.GetProperty("reason").GetString() == "configured_baseline_absent",
            "missing configured baseline is explicit"
        );
        var analysis = new AnalysisRecord
        {
            AnalysisId = Guid.NewGuid().ToString(),
            ProjectKey = key,
            Branch = scope.ReferenceBranch,
            CommitSha = manifest.TargetSha,
            CreatedAt = ApiJson.Iso(DateTime.UtcNow),
            Status = "completed",
        };
        var origin = manifest with
        {
            ProjectKey = key,
            CommitSha = analysis.CommitSha,
            Branch = analysis.Branch,
            RepositoryFiles = ["src/a.cs", "src/tests/a.cs", "src/private/key.cs"],
        };
        var ingestion = new IngestionRecord
        {
            AnalysisId = analysis.AnalysisId,
            ProjectKey = key,
            BuildKey = Guid.NewGuid().ToString(),
            ManifestHash = Guid.NewGuid().ToString(),
            State = "completed",
            TechnicalState = "complete",
            Revision = 1,
            Manifest = JsonSerializer.Serialize(origin, ApiJson.Options),
            CodeScopeSnapshot = serialized,
            CompletedAt = DateTime.UtcNow,
        };
        db.AddRange(
            analysis,
            ingestion,
            new CoverageLine
            {
                IngestionId = ingestion.Id,
                Path = "src/a.cs",
                Line = 1,
                Covered = true,
            }
        );
        var hotspot = new FindingOccurrence
        {
            AnalysisId = analysis.AnalysisId,
            IngestionId = ingestion.Id,
            Tool = "scanner",
            RuleId = "hotspot",
            Category = "security_hotspot",
            Message = "Review authorization",
            Severity = "high",
            Path = "src/a.cs",
            Line = 1,
        };
        db.Add(hotspot);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await FindingCollaboration.ObserveAsync(db, analysis, [hotspot], default);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var captured = await CodeScopes.CaptureAsync(db, key, default);
        Assert.True(
            captured.BaselineAnalysisId == analysis.AnalysisId
                && captured.BaselineSha == analysis.CommitSha,
            "configured baseline captures repository and exact commit"
        );
        var reviewed = await Call(
            "PUT",
            $"/api/v1/collaboration/findings/{hotspot.FindingId}",
            new
            {
                version = 1,
                state = "accepted_risk",
                hotspot_review = "safe",
                reason = "Isolated administrator approval",
                expires_at = DateTime.UtcNow.AddDays(1),
            },
            admin
        );
        string reviewSnapshot;
        var capturedAt = DateTime.UtcNow;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            reviewSnapshot = await SecurityReviewEvaluation.CaptureAsync(
                db,
                key,
                analysis.Branch,
                capturedAt,
                default
            );
            await transaction.CommitAsync();
        }
        Assert.True(
            JsonSerializer
                .Deserialize<SecurityReviewDecision[]>(reviewSnapshot, ApiJson.Options)!
                .Length == 1,
            "review snapshot persists actual administrator decision and retained evidence"
        );
        var reviewedProfile = new ProfileDefinition
        {
            ProfileId = key,
            Version = "3",
            RequiredTools = ["scanner"],
            RequiredMetrics = ["security_hotspots"],
            Rules = [new("scanner", "hotspot", "security_hotspot")],
            Conditions = [new("security_hotspots", "max", 0)],
            SecurityReview = new(true, 100),
        };
        await Call("POST", "/api/v1/profiles", reviewedProfile, admin, 201);
        await Call(
            "PUT",
            $"/api/v1/profiles/projects/{key}",
            new ProfileAssignment(key, "3"),
            admin
        );
        var accepted = await PublishHotspot(new string('b', 40));
        Assert.True(
            accepted.GetProperty("quality_gate").GetString() == "pass"
                && accepted
                    .GetProperty("gate_snapshot")
                    .GetProperty("security_review")
                    .GetProperty("applied_exception_decision_ids")
                    .GetArrayLength() == 1,
            "durable worker applies the snapshotted approval under the versioned policy"
        );
        await Call(
            "PUT",
            $"/api/v1/collaboration/findings/{hotspot.FindingId}",
            new
            {
                version = reviewed.GetProperty("version").GetInt32(),
                state = "open",
                hotspot_review = "not_reviewed",
                reason = "Approval revoked for next publication",
            },
            admin
        );
        var frozen = SecurityReviewEvaluation.Apply(
            new(true, 100),
            reviewSnapshot,
            origin,
            capturedAt,
            [hotspot],
            true
        );
        Assert.True(
            frozen.AppliedExceptionDecisionIds.Length == 1,
            "later revocation cannot alter a captured publication decision"
        );
        var unreviewed = await PublishHotspot(new string('c', 40));
        Assert.True(
            unreviewed.GetProperty("quality_gate").GetString() == "fail",
            "next publication observes revoked approval and pending hotspot review"
        );
        var currentFinding = await Call(
            "GET",
            $"/api/v1/collaboration/findings/{hotspot.FindingId}",
            token: admin
        );
        await Call(
            "PUT",
            $"/api/v1/collaboration/findings/{hotspot.FindingId}",
            new
            {
                version = currentFinding.GetProperty("version").GetInt32(),
                state = "accepted_risk",
                hotspot_review = "safe",
                reason = "Approval tied to the current evidence",
                expires_at = DateTime.UtcNow.AddDays(1),
            },
            admin
        );
        var replacement = await PublishHotspot(new string('d', 40), replaced: true);
        Assert.True(
            replacement.GetProperty("quality_gate").GetString() == "fail",
            "replaced line with unchanged scanner message cannot reuse an old approval"
        );
        var afterReplacement = await PublishHotspot(new string('e', 40));
        Assert.True(
            afterReplacement.GetProperty("quality_gate").GetString() == "fail",
            "third publication cannot resurrect approval by rebinding it to a newer proof"
        );
        var acceptedAgain = await Call(
            "GET",
            "/api/v1/ingestions/" + accepted.GetProperty("ingestion_id").GetString(),
            token: admin
        );
        Assert.True(
            acceptedAgain.GetProperty("quality_gate").GetString() == "pass",
            "review change preserves past verdict"
        );
        await Call(
            "POST",
            $"/api/v1/code/projects/{key}/versions",
            scope with
            {
                Version = "2",
                IncludedPaths = ["other/"],
            },
            admin,
            201
        );
        await Call(
            "PUT",
            $"/api/v1/code/projects/{key}/configuration",
            new { version = "2" },
            admin
        );
        await Call(
            "PUT",
            $"/api/v1/code/projects/{key}/branches",
            new { branch = analysis.Branch, archived = true },
            admin
        );
        var branches = await Call("GET", $"/api/v1/code/projects/{key}/branches", token: admin);
        Assert.True(
            branches[0].GetProperty("archived").GetBoolean()
                && await db.Set<DurableFinding>().AnyAsync(f => f.Id == hotspot.FindingId),
            "branch archive preserves durable reviews"
        );
        await db.Set<IngestionRecord>()
            .Where(i => i.Id == ingestion.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.DetailsPurged, true));
        Console.WriteLine(
            "Code scope HTTP checks passed: boundaries, immutable versions, frozen SHA, issue review, archives, permissions and retention."
        );

        async Task<JsonElement> PublishHotspot(string sha, bool replaced = false)
        {
            const string report =
                """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"scanner"}},"results":[{"ruleId":"hotspot","level":"error","message":{"text":"Review authorization"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":1}}}]}]}]}""";
            var bytes = Encoding.UTF8.GetBytes(report);
            var configured = await Call(
                "GET",
                $"/api/v1/code/projects/{key}/configuration",
                token: admin
            );
            var baseSha = configured.GetProperty("baseline_sha").GetString()!;
            var publication = new IngestionManifest
            {
                ProjectKey = key,
                RepositoryUrl = scope.RepositoryUrl,
                CodeScopeVersion = "1",
                Branch = analysis.Branch,
                CommitSha = sha,
                BuildId = key + sha[..6],
                CiProvider = "local",
                ProfileId = key,
                ProfileVersion = "3",
                RepositoryFiles = ["src/a.cs"],
                TargetSha = baseSha,
                GitDiff = new()
                {
                    State = "available",
                    BaseSha = baseSha,
                    HeadSha = sha,
                    Files =
                    [
                        new(
                            "src/a.cs",
                            null,
                            replaced ? [1] : [],
                            replaced ? [] : new() { ["1"] = 1 }
                        ),
                    ],
                },
                Reports =
                [
                    new(
                        "main",
                        "sarif",
                        "2.1.0",
                        "scanner",
                        "1",
                        "measured",
                        bytes.Length,
                        Convert.ToHexStringLower(SHA256.HashData(bytes))
                    ),
                ],
            };
            var receipt = await Call("POST", "/api/v1/ingestions", publication, admin, 201);
            var id = receipt.GetProperty("ingestion_id").GetString()!;
            using var upload = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/ingestions/{id}/reports/main"
            )
            {
                Content = new ByteArrayContent(bytes),
            };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var uploaded = await client.SendAsync(upload);
            Assert.True(uploaded.IsSuccessStatusCode, "hotspot SARIF upload");
            await Call("POST", $"/api/v1/ingestions/{id}/finalize", token: admin, status: 202);
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var result = await Call("GET", $"/api/v1/ingestions/{id}", token: admin);
                if (result.GetProperty("state").GetString() is "completed" or "failed")
                    return result;
                await Task.Delay(250);
            }
            throw new InvalidOperationException("Hotspot publication did not complete");
        }

        async Task<JsonElement> Call(
            string method,
            string path,
            object? body = null,
            string? token = null,
            int status = 200
        )
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: ApiJson.Options);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(
                (int)response.StatusCode == status,
                $"{method} {path}: expected {status}, got {(int)response.StatusCode}"
            );
            return string.IsNullOrWhiteSpace(text)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(text);
        }
    }

    private static void Invalid(Action action, int status)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected invalid scope");
        }
        catch (ApiException ex) when (ex.StatusCode == status) { }
    }

}
