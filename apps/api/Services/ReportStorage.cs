using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace CodeInsights.Api.Services;

public sealed class IngestionOptions
{
    public string BlobServiceUri { get; set; } = "";
    public string BlobConnectionString { get; set; } = "";
    public string Container { get; set; } = "reports";
    public string PlatformUrl { get; set; } = "";
    public long MaxReportBytes { get; set; } = 50 * 1024 * 1024;
    public long MaxManifestBytes { get; set; } = 250 * 1024 * 1024;
    public int Concurrency { get; set; } = 2;
    public int MaxAttempts { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 120;
    public int ParseTimeoutSeconds { get; set; } = 120;
    public int MaxSourceFiles { get; set; } = 1000;
    public long MaxSourceBytes { get; set; } = 2 * 1024 * 1024;
    public long MaxAnalysisSourceBytes { get; set; } = 50 * 1024 * 1024;
    public int PromptContextLines { get; set; } = 20;
}

public sealed class ReportStorage(IOptions<IngestionOptions> options)
{
    private BlobContainerClient Container()
    {
        var o = options.Value;
        var service = !string.IsNullOrEmpty(o.BlobConnectionString) ? new BlobServiceClient(o.BlobConnectionString)
            : !string.IsNullOrEmpty(o.BlobServiceUri) ? new BlobServiceClient(new Uri(o.BlobServiceUri), new DefaultAzureCredential())
            : throw new ApiException(503, "Report storage is not configured");
        return service.GetBlobContainerClient(o.Container);
    }

    public async Task UploadAsync(string name, Stream input, long expectedSize, string expectedHash, CancellationToken ct)
    {
        var container = Container();
        // Azurite needs its isolated container; Azure provisioning is owned by Terraform.
        if (!string.IsNullOrEmpty(options.Value.BlobConnectionString)) await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        var client = container.GetBlobClient(name);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = await client.OpenWriteAsync(true, cancellationToken: ct);
        var buffer = new byte[64 * 1024];
        long size = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                size += read;
                if (size > expectedSize || size > options.Value.MaxReportBytes) throw new ApiException(413, "Report size exceeds manifest");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (size != expectedSize || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(422, "Report length or SHA-256 does not match manifest");
            await output.FlushAsync(ct);
        }
        catch
        {
            await output.DisposeAsync();
            await client.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            throw;
        }
    }
    public async Task<Stream> OpenAsync(string name, CancellationToken ct) => await Container().GetBlobClient(name).OpenReadAsync(cancellationToken: ct);
    public async Task DeleteAsync(string name, CancellationToken ct) => await Container().GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: ct);
    public async IAsyncEnumerable<string> AbandonedAsync(DateTime cutoff, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var blob in Container().GetBlobsAsync(cancellationToken: ct))
            if (blob.Properties.LastModified < cutoff && blob.Name.Split('/') is { Length: 3 } parts && (parts[0] == "sources" && Guid.TryParse(parts[1], out _) && Guid.TryParse(parts[2], out _) || parts[0] != "sources" && Guid.TryParse(parts[1], out _) && Guid.TryParseExact(parts[2], "N", out _))) yield return blob.Name;
    }
}
