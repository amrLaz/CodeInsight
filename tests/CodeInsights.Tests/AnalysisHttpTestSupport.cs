using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Services;

public static class AnalysisHttpTestSupport
{
    private const string AnalysesEndpoint = "/api/v1/analyses";
    private const string SecurityScore = "security_score";
    private const string MeasurementStatus = "measured";
    private const string QualityGate = "quality_gate";

    public static async Task RunAsync(
        HttpClient client,
        string ciToken,
        string projectKey,
        string adminToken
    )
    {
        async Task<HttpResponseMessage> Send(
            HttpMethod method,
            string path,
            object? body = null,
            string? token = null
        )
        {
            var message = new HttpRequestMessage(method, path);
            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token ?? ciToken
            );
            if (body != null)
                message.Content = JsonContent.Create(body, options: ApiJson.Options);
            return await client.SendAsync(message);
        }
        static async Task<JsonElement> Json(
            HttpResponseMessage response,
            HttpStatusCode status = HttpStatusCode.OK
        )
        {
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != status)
                throw new InvalidOperationException(
                    $"Analysis HTTP {response.RequestMessage?.RequestUri}: expected {status}, got {response.StatusCode}: {text}"
                );
            return JsonDocument.Parse(text).RootElement.Clone();
        }

var complete = new AnalysisRequest
        {
            ProjectKey = projectKey,
            CommitSha = "abcdef012345",
            Branch = "migration-check",
            ProfileId = "p0-dotnet",
            ProfileVersion = "1",
            FormulaVersion = "1",
            ToolExecutions =
            [
                new("semgrep", "success", "1"),
                new("trivy", "success", "1"),
                new("gitleaks", "success", "1"),
            ],
        };
        foreach (var metric in GateEvaluation.GateMetrics)
            complete.Measurements[metric] = new(
                MeasurementStatus,
                metric.EndsWith("score") ? 100 : 0,
                "semgrep",
                "1"
            );
        complete.AffectedFiles = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"src/a.cs\":[{\"rule\":\"semgrep/csharp.sample-rule\",\"severity\":\"HIGH\",\"category\":\"vulnerability\",\"line\":4,\"message\":\"Sample issue\"}],\"sample.csproj\":[{\"rule\":\"trivy/CVE-2026-0001\",\"severity\":\"HIGH\",\"category\":\"dependency_vulnerability\",\"message\":\"Sample@1: title | fix: upgrade to 2 | CVSS 8.1\"}]}"
        );
        var cycle = JsonSerializer.Deserialize<AnalysisRequest>(
            JsonSerializer.Serialize(complete, ApiJson.Options),
            ApiJson.Options
        )!;
        cycle.CommitSha = "fedcba987654";
        cycle.Branch = "lifecycle-check";
        var providerRun = "http-check-" + Guid.NewGuid().ToString("N");
        var startBody = new AnalysisStartRequest
        {
            ProjectKey = projectKey,
            CommitSha = cycle.CommitSha,
            Branch = cycle.Branch,
            CiProvider = cycle.CiProvider,
            ProviderRunId = providerRun,
        };
        var started = await Json(await Send(HttpMethod.Post, "/api/v1/analyses/start", startBody));
        var lifecycleId = started.GetProperty("analysis_id").GetString()!;
        Assert.True(
            started.GetProperty("status").GetString() == "running"
                && started.GetProperty("quality_gate_result").GetProperty("status").GetString()
                    == "pending",
            "pending lifecycle"
        );
        var replayed = await Json(await Send(HttpMethod.Post, "/api/v1/analyses/start", startBody));
        Assert.True(replayed.GetProperty("analysis_id").GetString() == lifecycleId, "idempotent start");
        var sourceBytes = Encoding.UTF8.GetBytes("one\ntwo\nthree\nunsafe();");
        var declared = await Json(
            await Send(
                HttpMethod.Post,
                $"/api/v1/analyses/{lifecycleId}/sources",
                new
                {
                    sources = new[]
                    {
                        new
                        {
                            path = "src/a.cs",
                            size_bytes = sourceBytes.Length,
                            sha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
                        },
                    },
                }
            )
        );
        var sourceId = declared.GetProperty("sources")[0].GetProperty("source_id").GetString()!;
        using (
            var upload = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/analyses/{lifecycleId}/sources/{sourceId}"
            )
        )
        {
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ciToken);
            upload.Content = new ByteArrayContent(sourceBytes);
            using var uploaded = await client.SendAsync(upload);
            Assert.True(uploaded.StatusCode == HttpStatusCode.OK, "source upload");
        }
        var completedCycle = await Json(
            await Send(HttpMethod.Post, $"/api/v1/analyses/{lifecycleId}/complete", cycle)
        );
        Assert.True(
            completedCycle.GetProperty("status").GetString() == "completed",
            "completed lifecycle"
        );
        await Json(await Send(HttpMethod.Post, $"/api/v1/analyses/{lifecycleId}/complete", cycle));
        cycle.Measurements[SecurityScore] = new(MeasurementStatus, 0, "semgrep", "1");
        await Json(
            await Send(HttpMethod.Post, $"/api/v1/analyses/{lifecycleId}/complete", cycle),
            HttpStatusCode.Conflict
        );
        var lifecycleFindings = await Json(
            await Send(HttpMethod.Get, $"/api/v1/insights/analyses/{lifecycleId}/findings")
        );
        var occurrenceId = lifecycleFindings
            .EnumerateArray()
            .Single(x => x.GetProperty("path").GetString() == "src/a.cs")
            .GetProperty("id")
            .GetInt64();
        var prompt = await Json(
            await Send(HttpMethod.Get, $"/api/v1/issues/{occurrenceId}/prompt", token: adminToken)
        );
        Assert.True(
            prompt.GetProperty("prompt").GetString()!.Contains("    4: unsafe();"),
            "prompt source context"
        );
        var failedStart = await Json(
            await Send(
                HttpMethod.Post,
                "/api/v1/analyses/start",
                new AnalysisStartRequest
                {
                    ProjectKey = projectKey,
                    CommitSha = "001122334455",
                    Branch = "failure-check",
                    CiProvider = "local",
                    ProviderRunId = "http-fail-" + Guid.NewGuid().ToString("N"),
                }
            )
        );
        var failedId = failedStart.GetProperty("analysis_id").GetString()!;
        var failedCycle = await Json(
            await Send(
                HttpMethod.Post,
                $"/api/v1/analyses/{failedId}/fail",
                new { error_code = "scanner_failed" }
            )
        );
        Assert.True(
            failedCycle.GetProperty("status").GetString() == "failed"
                && failedCycle.GetProperty(QualityGate).GetString() == "unknown",
            "technical failure is not a quality failure"
        );
        var created = await Json(await Send(HttpMethod.Post, AnalysesEndpoint, complete));
        Assert.True(created.GetProperty(QualityGate).GetString() == "pass", "complete gate pass");
        var id = created.GetProperty("analysis_id").GetString()!;
        var detail = await Json(await Send(HttpMethod.Get, $"/api/v1/analyses/{id}"));
        Assert.True(
            detail
                .GetProperty("metrics")
                .GetProperty("affected_files")
                .GetProperty("src/a.cs")
                .GetArrayLength() == 1,
            "persisted findings"
        );
        var list = await Json(
            await Send(
                HttpMethod.Get,
                $"/api/v1/analyses?project_key={Uri.EscapeDataString(projectKey)}"
            )
        );
        Assert.True(
            list.EnumerateArray().Any(x => x.GetProperty("analysis_id").GetString() == id),
            "project list contains analysis"
        );
        list = await Json(await Send(HttpMethod.Get, AnalysesEndpoint));
        Assert.True(
            list.EnumerateArray()
                .First(x => x.GetProperty("analysis_id").GetString() == id)
                .GetProperty("metrics")
                .GetProperty("affected_files")
                .ValueKind == JsonValueKind.Null,
            "global list omits findings"
        );
        foreach (
            var path in new[]
            {
                $"/api/v1/analyses/{id}/export/csv",
                $"/api/v1/analyses/export/csv?project_key={Uri.EscapeDataString(projectKey)}",
            }
        )
        {
            using var csv = await Send(HttpMethod.Get, path);
            Assert.True(
                csv.StatusCode == HttpStatusCode.OK
                    && (await csv.Content.ReadAsStringAsync()).StartsWith(
                        "analysis_id,project_key"
                    ),
                "CSV export"
            );
        }
        using (
            var redirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = client.BaseAddress,
            }
        )
        {
            redirectClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                ciToken
            );
            using var redirect = await redirectClient.GetAsync($"/api/v1/analyses/{id}/view");
            var location = redirect.Headers.Location?.ToString() ?? "";
            // The link belongs to the configured hub; without one the route says so instead of
            // sending the reader to a development origin on their own machine.
            Assert.True(
                redirect.StatusCode == HttpStatusCode.ServiceUnavailable
                    || (
                        redirect.StatusCode == HttpStatusCode.Found
                        && location.EndsWith($"/?analysis_id={id}", StringComparison.Ordinal)
                        && !location.Contains("localhost:5174", StringComparison.Ordinal)
                    ),
                "analysis view redirect targets the configured hub"
            );
        }
        var rules = await Json(
            await Send(
                HttpMethod.Get,
                $"/api/v1/rules?project_key={Uri.EscapeDataString(projectKey)}"
            )
        );
        Assert.True(
            rules
                .EnumerateArray()
                .Any(x => x.GetProperty("rule_id").GetString() == "semgrep/csharp.sample-rule"),
            "rule aggregate"
        );
        Assert.True(
            !rules
                .EnumerateArray()
                .Any(x => x.GetProperty("rule_id").GetString()!.StartsWith("trivy/")),
            "SCA excluded from rules"
        );
        await Json(await Send(HttpMethod.Get, "/api/v1/rules"));
        await Json(
            await Send(
                HttpMethod.Post,
                "/api/v1/rules/semgrep/csharp.sample-rule/suppress?note=migration-test",
                token: adminToken
            )
        );
        rules = await Json(
            await Send(
                HttpMethod.Get,
                $"/api/v1/rules?project_key={Uri.EscapeDataString(projectKey)}"
            )
        );
        Assert.True(
            rules
                .EnumerateArray()
                .First(x => x.GetProperty("rule_id").GetString() == "semgrep/csharp.sample-rule")
                .GetProperty("status")
                .GetString() == "suppressed",
            "rule suppression visible"
        );
        await Json(
            await Send(
                HttpMethod.Delete,
                "/api/v1/rules/semgrep/csharp.sample-rule/suppress",
                token: adminToken
            )
        );
        complete.Measurements[SecurityScore] = new(MeasurementStatus, 0);
        var failed = await Json(await Send(HttpMethod.Post, AnalysesEndpoint, complete));
        Assert.True(failed.GetProperty(QualityGate).GetString() == "fail", "measured failure");
        complete.Measurements[SecurityScore] = new(MeasurementStatus, 100);
        complete.FormulaVersion = "unknown";
        var unknown = await Json(await Send(HttpMethod.Post, AnalysesEndpoint, complete));
        Assert.True(
            unknown.GetProperty(QualityGate).GetString() == "unknown",
            "unknown formula cannot pass"
        );
        complete.ProfileId = "";
        await Json(
            await Send(HttpMethod.Post, AnalysesEndpoint, complete),
            HttpStatusCode.UnprocessableEntity
        );
        complete.ProfileId = "p0-dotnet";
        complete.FormulaVersion = "1";
        complete.Measurements[SecurityScore] = new(MeasurementStatus, 101);
        await Json(
            await Send(HttpMethod.Post, AnalysesEndpoint, complete),
            HttpStatusCode.UnprocessableEntity
        );
        complete.Measurements[SecurityScore] = new(MeasurementStatus, 100);
        var strict = JsonSerializer.SerializeToNode(complete, ApiJson.Options)!.AsObject();
        foreach (
            var removedField in new[]
            {
                "profile_id",
                "profile_version",
                "formula_version",
                "measurements",
                "tool_executions",
            }
        )
        {
            var missing = strict.DeepClone().AsObject();
            missing.Remove(removedField);
            await Json(
                await Send(HttpMethod.Post, AnalysesEndpoint, missing),
                HttpStatusCode.BadRequest
            );
        }
        foreach (var unknownField in new[] { "unexpected_field" })
        {
            var extra = strict.DeepClone().AsObject();
            extra[unknownField] = 1;
            await Json(
                await Send(HttpMethod.Post, AnalysesEndpoint, extra),
                HttpStatusCode.BadRequest
            );
        }
        await Json(await Send(HttpMethod.Get, "/api/v1/analyses/missing"), HttpStatusCode.NotFound);
        using (
            var unauthorized = await Send(
                HttpMethod.Get,
                $"/api/v1/analyses/{id}",
                token: "invalid"
            )
        )
            Assert.True(
                unauthorized.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                "anonymous denied"
            );
        using (
            var forbidden = await Send(
                HttpMethod.Post,
                "/api/v1/rules/semgrep/csharp.sample-rule/suppress",
                token: ciToken
            )
        )
            Assert.True(
                forbidden.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                "CI rule mutation denied"
            );
        using (
            var removedBranchDelete = await Send(
                HttpMethod.Delete,
                $"/api/v1/analyses/projects/{Uri.EscapeDataString(projectKey)}/branches/migration-check",
                token: adminToken
            )
        )
            Assert.True(
                removedBranchDelete.StatusCode == HttpStatusCode.NotFound,
                "branch analysis deletion endpoint removed"
            );
        await Json(await Send(HttpMethod.Get, $"/api/v1/analyses/{id}"));
        await Json(
            await Send(
                HttpMethod.Delete,
                $"/api/v1/analyses/projects/{Uri.EscapeDataString(projectKey)}",
                token: adminToken
            )
        );
        await Json(await Send(HttpMethod.Get, $"/api/v1/analyses/{id}"), HttpStatusCode.NotFound);
        Console.WriteLine(
            "Analysis HTTP CRUD, gates, findings, rules, exports, errors and access checks passed."
        );
    }
}
