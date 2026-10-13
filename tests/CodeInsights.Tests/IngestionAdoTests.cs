using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;

public sealed class IngestionAdoTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        Assert.True(
            RepositoryUrls.Normalize(
                "azure-devops",
                "https://up-group@dev.azure.com/up-group/Up.France.ODI/_git/repo/"
            ) == "https://dev.azure.com/up-group/Up.France.ODI/_git/repo",
            "Azure Repos clone username normalized"
        );
        Assert.True(
            RepositoryUrls.Normalize("github-actions", " https://github.com/org/repo ")
                == "https://github.com/org/repo",
            "other providers preserved"
        );
        RejectsRepositoryUrl("https://token@dev.azure.com/up-group/project/_git/repo");
        RejectsRepositoryUrl("https://up-group:token@dev.azure.com/up-group/project/_git/repo");
        RejectsRepositoryUrl("https://up-group@dev.azure.com/up-group/project/_git/repo?token=bad");
        Assert.True(
            IngestionAdo.SameRepository(
                "https://dev.azure.com/org/project/_git/repo/",
                "https://dev.azure.com/org/project/_git/repo"
            ),
            "canonical repository"
        );
        Assert.True(
            !IngestionAdo.SameRepository(
                "https://dev.azure.com/org/project/_git/repo",
                "https://dev.azure.com/other/project/_git/repo"
            ),
            "organization boundary"
        );
        Assert.True(
            !IngestionAdo.SameRepository(
                "https://dev.azure.com/org/project/_git/repo",
                "https://dev.azure.com/org/project/_git/repo?redirect=bad"
            ),
            "query boundary"
        );
        var manifest = new IngestionManifest
        {
            RepositoryUrl = "https://dev.azure.com/org/project/_git/repo",
            PrRepoId = "repo",
            SourceSha = "aaaaaa",
            TargetSha = "bbbbbb",
            CommitSha = "cccccc",
        };
        var row = new IngestionRecord
        {
            AnalysisId = "fixture",
            Revision = 1,
            State = "completed",
            QualityGate = "fail",
            TechnicalState = "complete",
            GateSnapshot =
                """{"conditions":[{"metric":"bugs","operator":"<=","threshold":0,"value":2,"result":"fail"}]}""",
            Comparison =
                """{"state":"available","new_count":2,"persistent_count":3,"resolved_count":1,"indeterminate_count":0,"coverage":50,"findings":[{"tool":"scanner","rule_id":"[rule]","path":"src/<file>.cs","line":4,"state":"new"}]}""",
        };
        var handler = new Handler();
        using var client = new HttpClient(handler);
        const string url = "https://ado.invalid/pr/1";
        async Task<bool> Deliver(string enforcement = "informative") =>
            await IngestionAdo.DeliverAsync(
                client,
                url,
                manifest,
                row,
                enforcement,
                "https://hub.invalid",
                CancellationToken.None
            );
        Assert.True(await Deliver(), "current merge commit delivered");
        Assert.True(
            handler.Status?.GetProperty("state").GetString() == "succeeded"
                && handler.Status?.GetProperty("iterationId").GetInt32() == 3,
            "informative quality failure scoped to iteration"
        );
        Assert.True(handler.ThreadPosts == 1 && handler.StatusPosts == 1, "initial objects created");
        Assert.True(
            handler.Thread!.Contains("bugs: 2 &lt;= 0 (fail)")
                && handler.Thread.Contains("new 2, persistent 3, resolved 1, indeterminate 0")
                && handler.Thread.Contains("scanner/\\[rule\\]")
                && handler.Thread.Contains("src/&lt;file&gt;.cs:4"),
            "comment preserves exact gate/comparison values and escapes report content"
        );
        await Deliver();
        Assert.True(
            handler.ThreadPosts == 1 && handler.StatusPosts == 1,
            "identical status replay creates no history entry"
        );
        await Deliver("blocking");
        Assert.True(
            handler.Status?.GetProperty("state").GetString() == "failed"
                && handler.ThreadPosts == 1
                && handler.StatusPosts == 2
                && handler.CommentPatches == 2,
            "changed status uses stable context and existing comment"
        );
        row.TechnicalState = "incomplete";
        row.Comparison = "{\"state\":\"unavailable\",\"reason\":\"baseline_absent\"}";
        await Deliver();
        Assert.True(
            handler.Status?.GetProperty("state").GetString() == "error",
            "informative incomplete cannot pass"
        );
        Assert.True(
            handler.Thread!.Contains("New Code unavailable: baseline\\_absent"),
            "comparison unavailable reason shown"
        );
        row.TechnicalState = "complete";
        row.State = "queued";
        await Deliver();
        Assert.True(
            handler.Status?.GetProperty("state").GetString() == "pending",
            "new finalized revision resets status pending"
        );
        row.State = "completed";
        row.QualityGate = "unknown";
        await Deliver();
        Assert.True(handler.Status?.GetProperty("state").GetString() == "error", "unknown cannot pass");
        handler.Obsolete = true;
        var mutations = handler.Mutations;
        Assert.True(
            !await Deliver() && handler.Mutations == mutations,
            "old source skipped without writes"
        );
        handler.Obsolete = false;
        handler.WrongMerge = true;
        Assert.True(!await Deliver() && handler.Mutations == mutations, "unverified merge skipped");
        handler.WrongMerge = false;
        handler.WrongRepository = true;
        await Throws<InvalidDataException>(() => Deliver());
        Assert.True(handler.Mutations == mutations, "wrong repository not mutated");
        handler.WrongRepository = false;
        handler.RateLimit = true;
        await Throws<HttpRequestException>(() => Deliver());
        handler.RateLimit = false;
        handler.Timeout = true;
        await Throws<TaskCanceledException>(() => Deliver());
        handler.Timeout = false;
        handler.Thread = null;
        handler.LoseThreadResponse = true;
        await Throws<HttpRequestException>(() => Deliver());
        var postsAfterLoss = handler.ThreadPosts;
        await Deliver();
        Assert.True(
            handler.ThreadPosts == postsAfterLoss,
            "uncertain POST response reconciled by reading marker"
        );
        Console.WriteLine("Durable Azure DevOps delivery checks passed (simulated HTTP only).");
    }

    private static void RejectsRepositoryUrl(string value)
    {
        try
        {
            RepositoryUrls.Normalize("azure-devops", value);
        }
        catch (ApiException ex) when (ex.StatusCode == 422)
        {
            return;
        }
        throw new InvalidOperationException("Expected repository URL rejection: " + value);
    }

    private static async Task Throws<T>(Func<Task<bool>> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

private sealed class Handler : HttpMessageHandler
    {
        public JsonElement? Status;
        public string? Thread;
        public bool Obsolete,
            WrongMerge,
            WrongRepository,
            RateLimit,
            Timeout,
            LoseThreadResponse;
        public int ThreadPosts,
            StatusPosts,
            CommentPatches,
            Mutations;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            if (Timeout)
                throw new TaskCanceledException("Synthetic timeout");
            if (RateLimit)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/iterations"))
                    return Json(
                        new
                        {
                            value = new[]
                            {
                                new
                                {
                                    id = 3,
                                    sourceRefCommit = new { commitId = "aaaaaa" },
                                    targetRefCommit = new { commitId = "bbbbbb" },
                                },
                            },
                        }
                    );
                if (path.EndsWith("/statuses"))
                    return Status is { } status
                        ? Json(
                            new
                            {
                                value = new[]
                                {
                                    new
                                    {
                                        id = 7,
                                        iterationId = 3,
                                        context = status.GetProperty("context"),
                                        state = status.GetProperty("state"),
                                        description = status.GetProperty("description"),
                                        targetUrl = status.GetProperty("targetUrl"),
                                    },
                                },
                            }
                        )
                        : Json(new { value = Array.Empty<object>() });
                if (path.EndsWith("/threads"))
                    return Thread is { } content
                        ? Json(
                            new
                            {
                                value = new[]
                                {
                                    new { id = 8, comments = new[] { new { id = 1, content } } },
                                },
                            }
                        )
                        : Json(new { value = Array.Empty<object>() });
                return Json(
                    new
                    {
                        status = "active",
                        lastMergeSourceCommit = new { commitId = Obsolete ? "changed" : "aaaaaa" },
                        lastMergeTargetCommit = new { commitId = "bbbbbb" },
                        lastMergeCommit = new { commitId = WrongMerge ? "different" : "cccccc" },
                        repository = new
                        {
                            id = WrongRepository ? "another" : "repo",
                            remoteUrl = "https://org@dev.azure.com/org/project/_git/repo",
                        },
                    }
                );
            }
            Mutations++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (path.Contains("/statuses"))
            {
                Status = body.RootElement.Clone();
                if (request.Method == HttpMethod.Post)
                    StatusPosts++;
            }
            else if (path.EndsWith("/threads"))
            {
                ThreadPosts++;
                Thread = body
                    .RootElement.GetProperty("comments")[0]
                    .GetProperty("content")
                    .GetString();
                if (LoseThreadResponse)
                {
                    LoseThreadResponse = false;
                    throw new HttpRequestException("Synthetic lost response");
                }
            }
            else
            {
                CommentPatches++;
                Thread = body.RootElement.GetProperty("content").GetString();
            }
            return Json(new { id = 1 });
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
    }
}
