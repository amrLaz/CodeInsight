using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeInsights.Api;
using CodeInsights.Api.Services;

public sealed class AzureDevOpsSourceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var repository = AzureDevOpsSource.Repository(
            "https://dev.azure.com/up-france/Platform/_git/CodeInsights"
        );
        Assert.True(repository.OrganizationUrl == "https://dev.azure.com/up-france", "Organization parsed");
        Assert.True(repository.Project == "Platform", "Project parsed");
        Assert.True(repository.Repository == "CodeInsights", "Repository parsed");

        var success = new SourceHandler(HttpStatusCode.OK, Encoding.UTF8.GetBytes("one\r\ntwo\rthree"));
        using (var client = new HttpClient(success, disposeHandler: false))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "managed-identity-token");
            var content = await AzureDevOpsSource.DownloadAsync(
                client,
                repository,
                Sha,
                "src/a file.cs",
                100,
                CancellationToken.None
            );
            Assert.True(content == "one\ntwo\nthree", "Line endings normalized");
        }
        Assert.True(success.Authorization == "Bearer managed-identity-token", "Bearer authorization preserved");
        Assert.True(success.Accept == "text/plain", "Text response requested");
        Assert.True(success.Redirect == "Suppress", "Interactive authentication redirect suppressed");
        Assert.True(
            Uri.UnescapeDataString(success.Uri?.Query ?? "").Contains(
                "path=/src/a file.cs",
                StringComparison.Ordinal
            )
                && success.Uri?.Query.Contains($"versionDescriptor.version={Sha}", StringComparison.Ordinal) == true
                && success.Uri?.Query.Contains("versionDescriptor.versionType=commit", StringComparison.Ordinal) == true,
            "Exact path and commit requested"
        );

        await RejectAsync(HttpStatusCode.NotFound, [1], 404);
        await RejectAsync(HttpStatusCode.Unauthorized, [1], 502);
        await RejectAsync((HttpStatusCode)429, [1], 503);
        await RejectAsync(HttpStatusCode.OK, [0], 415);
        await RejectAsync(HttpStatusCode.OK, [0xff], 415);
        await RejectAsync(HttpStatusCode.OK, Encoding.UTF8.GetBytes("too large"), 413, 3);

        var untouched = new SourceHandler(HttpStatusCode.OK, [1]);
        using var invalidClient = new HttpClient(untouched, disposeHandler: false);
        await ExpectAsync(
            () => AzureDevOpsSource.DownloadAsync(
                invalidClient,
                repository,
                "short",
                "src/a.cs",
                100,
                CancellationToken.None
            ),
            409
        );
        Assert.True(untouched.Requests == 0, "Invalid commit rejected before HTTP");
        Console.WriteLine("Azure DevOps source checks passed (exact commit, bounds, UTF-8, failures).");
    }

    private static async Task RejectAsync(
        HttpStatusCode status,
        byte[] bytes,
        int expected,
        long maximumBytes = 100
    )
    {
        var repository = AzureDevOpsSource.Repository(
            "https://dev.azure.com/up-france/Platform/_git/CodeInsights"
        );
        var handler = new SourceHandler(status, bytes);
        using var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "managed-identity-token");
        await ExpectAsync(
            () => AzureDevOpsSource.DownloadAsync(
                client,
                repository,
                Sha,
                "src/a.cs",
                maximumBytes,
                CancellationToken.None
            ),
            expected
        );
    }

    private static async Task ExpectAsync(Func<Task<string>> action, int status)
    {
        try
        {
            await action();
        }
        catch (ApiException exception) when (exception.StatusCode == status)
        {
            return;
        }
        throw new InvalidOperationException($"Expected source failure {status}.");
    }

private sealed class SourceHandler(HttpStatusCode status, byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Accept { get; private set; }
        public string? Redirect { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.Accept.ToString();
            Redirect = request.Headers.GetValues("X-TFS-FedAuthRedirect").Single();
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }
            );
        }
    }
}
