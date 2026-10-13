using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeInsights.Api.Services;

/// <summary>Versioned repository/component boundaries. Baseline fields remain for legacy readers only.</summary>
public static class CodeScopes
{
    public static void Validate(CodeScopeDefinition value)
    {
        if (string.IsNullOrWhiteSpace(value.Version) || value.Version.Length > 50 || string.IsNullOrWhiteSpace(value.Component) || value.Component.Length > 200
            || FindingEvidenceLinks.SafeHttps(value.RepositoryUrl) is null || new Uri(value.RepositoryUrl).Query.Length > 0 || new Uri(value.RepositoryUrl).Fragment.Length > 0
            || !Branch(value.MainBranch) || value.ReferenceKind is not ("branch" or "analysis")
            || value.ReferenceKind == "branch" && !Branch(value.ReferenceBranch)
            || value.ReferenceKind == "analysis" && string.IsNullOrWhiteSpace(value.ReferenceAnalysisId))
            throw new ApiException(422, "Invalid code scope identity, repository or reference");
        foreach (var paths in new[] { value.IncludedPaths, value.ExcludedPaths, value.TestPaths, value.GeneratedPaths, value.ThirdPartyPaths })
            if (paths is null || paths.Length > 200 || paths.Any(p => !IngestionValidation.SafePath(p?.TrimEnd('/'))))
                throw new ApiException(422, "Code scope paths must be repository-relative file or directory prefixes");
    }

    public static bool Branch(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 500 && !value.Any(char.IsControl);
    private static bool Matches(string path, IEnumerable<string> patterns) => patterns.Any(p => p.EndsWith('/') ? path.StartsWith(p, StringComparison.Ordinal) : path == p);
    public static bool Includes(CodeScopeDefinition? scope, string? path) => scope is null || path is null
        || (scope.IncludedPaths.Length == 0 || Matches(path, scope.IncludedPaths)) && !Matches(path, scope.ExcludedPaths);
    public static string Kind(CodeScopeDefinition? scope, string path) => scope is null ? "source"
        : Matches(path, scope.GeneratedPaths) ? "generated" : Matches(path, scope.ThirdPartyPaths) ? "third_party" : Matches(path, scope.TestPaths) ? "test" : "source";
    public static bool IsSource(CodeScopeDefinition? scope, string? path) => Includes(scope, path) && (path is null || Kind(scope, path) == "source");

    public static async Task<CodeScopeSnapshot> CaptureAsync(AppDbContext db, string key, CancellationToken ct)
    {
        var assignment = await db.Set<Data.CodeScopeAssignment>().FindAsync([key], ct);
        if (assignment is null) return new(null, null, null, null);
        var row = await db.Set<CodeScopeVersion>().FindAsync([key, assignment.Version], ct) ?? throw new ApiException(409, "Assigned code scope is missing");
        var scope = JsonSerializer.Deserialize<CodeScopeDefinition>(row.Definition, ApiJson.Options)!;
        var query = db.Set<AnalysisRecord>().AsNoTracking().Where(a => a.ProjectKey == key && a.Status == "completed");
        query = scope.ReferenceKind == "analysis" ? query.Where(a => a.AnalysisId == scope.ReferenceAnalysisId) : query.Where(a => a.Branch == scope.ReferenceBranch);
        // Only an imported analysis carries immutable repository provenance.
        var baseline = await (from a in query join i in db.Set<IngestionRecord>() on a.AnalysisId equals i.AnalysisId
            where i.State == "completed" orderby a.CreatedAt descending, i.Revision descending select new { a.AnalysisId, a.CommitSha, i.Manifest }).Take(100).ToListAsync(ct);
        var candidate = baseline.FirstOrDefault(x => JsonSerializer.Deserialize<IngestionManifest>(x.Manifest, ApiJson.Options)?.RepositoryUrl.TrimEnd('/') == scope.RepositoryUrl.TrimEnd('/'));
        return new(scope, candidate?.CommitSha, candidate?.AnalysisId, candidate is null ? "configured_baseline_absent" : null);
    }

    public static void ValidatePublication(IngestionManifest manifest, CodeScopeSnapshot snapshot)
    {
        if (manifest.CodeScopeVersion is null || manifest.CodeScopeVersion.Length > 50 || manifest.CodeScopeVersion.Any(char.IsControl)
            || manifest.RepositoryFiles is null || manifest.RepositoryFiles.Length > 100_000 || manifest.RepositoryFiles.Any(p => !IngestionValidation.SafePath(p))
            || manifest.RepositoryFiles.Distinct(StringComparer.Ordinal).Count() != manifest.RepositoryFiles.Length)
            throw new ApiException(422, "Invalid or oversized repository file inventory");
        if (snapshot.Definition is not { } scope)
        {
            if (!string.IsNullOrEmpty(manifest.CodeScopeVersion)) throw new ApiException(422, "No code scope assigned to project");
            return;
        }
        if (manifest.CodeScopeVersion != scope.Version || manifest.RepositoryUrl.TrimEnd('/') != scope.RepositoryUrl.TrimEnd('/'))
            throw new ApiException(422, "Publication code scope or repository differs from project assignment");
    }

    public static string CompatibilityKey(CodeScopeDefinition? value) => value is null
        ? "null"
        : JsonSerializer.Serialize(new
        {
            value.Version,
            value.RepositoryUrl,
            value.Component,
            value.IncludedPaths,
            value.ExcludedPaths,
            value.TestPaths,
            value.GeneratedPaths,
            value.ThirdPartyPaths,
        }, ApiJson.Options);

    public static CodeScopeSnapshot Read(string json) => JsonSerializer.Deserialize<CodeScopeSnapshot>(json, ApiJson.Options) ?? new(null, null, null, null);
}
