using System.Buffers;
using System.Net;
using System.Text;
using CodeInsights.Api.Contracts;

namespace CodeInsights.Api.Services;

/// <summary>Identifies one Azure Repos Git repository.</summary>
public sealed record AzureDevOpsRepository(
    string OrganizationUrl,
    string Project,
    string Repository
);

/// <summary>Reads one bounded text file from an Azure Repos commit without persisting it.</summary>
public static class AzureDevOpsSource
{
    /// <summary>Parses a normalized Azure DevOps repository URL.</summary>
    public static AzureDevOpsRepository Repository(string repositoryUrl)
    {
        var normalized = RepositoryUrls.Normalize("azure-devops", repositoryUrl);
        var uri = new Uri(normalized);
        var segments = Uri.UnescapeDataString(uri.AbsolutePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new(
            $"https://dev.azure.com/{Uri.EscapeDataString(segments[0])}",
            segments[1],
            segments[3]
        );
    }

    /// <summary>Downloads one bounded UTF-8 source file at an exact commit.</summary>
    public static async Task<string> DownloadAsync(
        HttpClient client,
        AzureDevOpsRepository repository,
        string commitSha,
        string path,
        long maximumBytes,
        CancellationToken ct
    )
    {
        if (commitSha.Length != 40 || !commitSha.All(Uri.IsHexDigit))
            throw new ApiException(409, "Source browsing requires the exact 40-character Azure DevOps commit SHA");
        if (!IngestionValidation.SafePath(path))
            throw new ApiException(422, "path must be a repository-relative path");
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            throw new InvalidOperationException("Invalid source size limit.");

        var url =
            $"{repository.OrganizationUrl}/{Uri.EscapeDataString(repository.Project)}"
            + $"/_apis/git/repositories/{Uri.EscapeDataString(repository.Repository)}/items"
            + $"?path={Uri.EscapeDataString('/' + path)}"
            + $"&versionDescriptor.version={commitSha}"
            + "&versionDescriptor.versionType=commit&download=true&api-version=7.1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/plain");
        request.Headers.Add("X-TFS-FedAuthRedirect", "Suppress");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ApiException(404, "Source file was not found at the analyzed commit");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ApiException(502, "Azure DevOps rejected the managed identity");
        if ((int)response.StatusCode == 429)
            throw new ApiException(503, "Azure DevOps source browsing is temporarily unavailable");
        if (!response.IsSuccessStatusCode)
            throw new ApiException(502, "Azure DevOps could not return the analyzed source file");
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new ApiException(413, $"Source file exceeds the {maximumBytes}-byte display limit");

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 81_920));
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                    throw new ApiException(413, $"Source file exceeds the {maximumBytes}-byte display limit");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
        if (bytes.Contains((byte)0))
            throw new ApiException(415, "Source file is not displayable text");
        try
        {
            return new UTF8Encoding(false, true)
                .GetString(bytes)
                .TrimStart('\uFEFF')
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }
        catch (DecoderFallbackException)
        {
            throw new ApiException(415, "Source file is not valid UTF-8 text");
        }
    }
}
