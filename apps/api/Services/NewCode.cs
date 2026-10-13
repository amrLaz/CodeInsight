using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

public static class NewCode
{
    private static readonly Dictionary<string, string[]> AllowedModes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["global"] = ["previous_version", "number_of_days"],
            ["project"] = ["previous_version", "number_of_days", "reference_branch"],
            ["branch"] = ["previous_version", "number_of_days", "reference_branch", "specific_analysis"],
        };

    public static string ScopeKey(string level, string? projectKey, string? branch) => level switch
    {
        "global" => "global",
        "project" => "project:" + projectKey,
        "branch" => "branch:" + projectKey + ":" + BranchRefs.Canonical(branch ?? ""),
        _ => throw new ApiException(422, "Invalid New Code definition level"),
    };

    internal static bool ModeAllowed(string level, string mode) =>
        AllowedModes.TryGetValue(level, out var modes) && modes.Contains(mode);

    internal static string? ConventionalMainBranch(string? branch)
    {
        // ponytail: infer only the two conventional names; use provider repository metadata if other defaults must be detected.
        var canonical = BranchRefs.Canonical(branch);
        return BranchRefs.Short(canonical) is "main" or "master" ? canonical : null;
    }

    public static async Task<string?> ResolveMainBranchAsync(
        AppDbContext db,
        string projectKey,
        CancellationToken ct
    )
    {
        var mainBranch = await db.Set<Project>().AsNoTracking()
            .Where(x => x.Key == projectKey)
            .Select(x => x.MainBranch)
            .SingleOrDefaultAsync(ct);
        if (mainBranch is not null)
            return mainBranch;
        var assignment = await db.Set<Data.CodeScopeAssignment>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectKey == projectKey, ct);
        if (assignment is null)
            return null;
        var legacy = await db.Set<CodeScopeVersion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectKey == projectKey && x.Version == assignment.Version, ct);
        return legacy is null
            ? null
            : JsonSerializer.Deserialize<CodeScopeDefinition>(legacy.Definition, ApiJson.Options)?.MainBranch;
    }

    internal static (EffectiveNewCodeDefinition Definition, string Source) SelectEffective(
        IEnumerable<NewCodeDefinition> definitions,
        string branch,
        string? prTargetBranch,
        string? manifestReferenceBranch
    )
    {
        branch = BranchRefs.Canonical(branch);
        if (!string.IsNullOrWhiteSpace(prTargetBranch))
            return (new("reference_branch", null, BranchRefs.Canonical(prTargetBranch), null), "pull_request_target");
        if (!string.IsNullOrWhiteSpace(manifestReferenceBranch))
            return (new("reference_branch", null, BranchRefs.Canonical(manifestReferenceBranch), null), "manifest_override");
        var definition = definitions.FirstOrDefault(x => x.Level == "branch" && x.Branch == branch)
            ?? definitions.FirstOrDefault(x => x.Level == "project")
            ?? definitions.FirstOrDefault(x => x.Level == "global");
        return definition is null
            ? (new("previous_version", null, null, null), "default")
            : (new(definition.Mode, definition.NumberOfDays, definition.ReferenceBranch, definition.AnalysisId), definition.Level);
    }

    public static async Task<NewCodeDefinition> ValidateAsync(
        AppDbContext db,
        NewCodeDefinitionRequest request,
        CancellationToken ct
    )
    {
        var level = request.Level.Trim().ToLowerInvariant();
        if (!ModeAllowed(level, request.Mode))
            throw new ApiException(422, "New Code mode is not allowed at this level");
        var projectKey = string.IsNullOrWhiteSpace(request.ProjectKey) ? null : request.ProjectKey.Trim();
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? null : BranchRefs.Canonical(request.Branch);
        if (level == "global" && (projectKey is not null || branch is not null)
            || level == "project" && (projectKey is null || branch is not null)
            || level == "branch" && (projectKey is null || branch is null))
            throw new ApiException(422, "New Code definition scope does not match its level");
        if (projectKey is not null && !await db.Set<Project>().AnyAsync(x => x.Key == projectKey, ct))
            throw new ApiException(404, "Project not found");
        if (request.Mode == "number_of_days" && request.NumberOfDays is not (>= 1 and <= 90))
            throw new ApiException(422, "number_of_days must be between 1 and 90");
        if (request.Mode != "number_of_days" && request.NumberOfDays is not null)
            throw new ApiException(422, "number_of_days is only valid for number_of_days mode");

        var referenceBranch = string.IsNullOrWhiteSpace(request.ReferenceBranch)
            ? null
            : BranchRefs.Canonical(request.ReferenceBranch);
        if (request.Mode == "reference_branch" && referenceBranch is null
            || request.Mode != "reference_branch" && referenceBranch is not null)
            throw new ApiException(422, "reference_branch must be set only for reference_branch mode");
        if (level == "branch" && referenceBranch == branch)
            throw new ApiException(422, "A branch cannot use itself as its New Code reference");
        if (level == "project" && referenceBranch is not null)
        {
            var mainBranch = await ResolveMainBranchAsync(db, projectKey!, ct);
            if (mainBranch is not null && BranchRefs.Canonical(mainBranch) == referenceBranch)
                throw new ApiException(422, "A project cannot use its main branch as its own New Code reference");
        }

        var analysisId = string.IsNullOrWhiteSpace(request.AnalysisId) ? null : request.AnalysisId.Trim();
        if (request.Mode == "specific_analysis" && analysisId is null
            || request.Mode != "specific_analysis" && analysisId is not null)
            throw new ApiException(422, "analysis_id must be set only for specific_analysis mode");
        if (analysisId is not null)
        {
            var analysis = await db.Set<AnalysisRecord>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.AnalysisId == analysisId, ct)
                ?? throw new ApiException(422, "Specific analysis does not exist");
            if (analysis.ProjectKey != projectKey || analysis.Branch != branch || analysis.Status != "completed")
                throw new ApiException(422, "Specific analysis must be completed on the configured project and branch");
            var repositories = await db.Set<ProjectIntegration>().AsNoTracking()
                .Where(x => x.ProjectKey == projectKey && x.RepoUrl != "")
                .Select(x => x.RepoUrl)
                .ToListAsync(ct);
            if (repositories.Count > 0
                && !repositories.Any(x => RepositoryUrls.Identity(x) == RepositoryUrls.Identity(analysis.RepositoryUrl)))
                throw new ApiException(422, "Specific analysis repository does not match the project integration");
        }

        return new NewCodeDefinition
        {
            ScopeKey = ScopeKey(level, projectKey, branch),
            Level = level,
            ProjectKey = projectKey,
            Branch = branch,
            Mode = request.Mode,
            NumberOfDays = request.NumberOfDays,
            ReferenceBranch = referenceBranch,
            AnalysisId = analysisId,
        };
    }

    public static async Task<NewCodeResolution> ResolveAsync(
        AppDbContext db,
        string projectKey,
        string repositoryUrl,
        string branch,
        string? projectVersion,
        string? prTargetBranch,
        string? manifestReferenceBranch,
        DateTime now,
        CancellationToken ct
    )
    {
        branch = BranchRefs.Canonical(branch);
        var definitions = string.IsNullOrWhiteSpace(prTargetBranch) && string.IsNullOrWhiteSpace(manifestReferenceBranch)
            ? await db.Set<NewCodeDefinition>().AsNoTracking()
                .Where(x => x.ScopeKey == "global" || x.ProjectKey == projectKey)
                .ToListAsync(ct)
            : [];
        var selected = SelectEffective(definitions, branch, prTargetBranch, manifestReferenceBranch);
        var source = selected.Source;
        var effective = selected.Definition;

        if (effective.Mode == "reference_branch" && effective.ReferenceBranch == branch)
            throw new ApiException(422, "A branch cannot use itself as its New Code reference");
        if (effective.Mode == "previous_version" && string.IsNullOrWhiteSpace(projectVersion))
            return Empty(effective, source, "project_version_missing");

        var query =
            from ingestion in db.Set<IngestionRecord>().AsNoTracking()
            join analysis in db.Set<AnalysisRecord>().AsNoTracking()
                on ingestion.AnalysisId equals analysis.AnalysisId
            where ingestion.ProjectKey == projectKey
                && ingestion.State == "completed"
                && analysis.Status == "completed"
                // A pull request build analyses a merge result, never the branch itself: it is not a baseline.
                && (analysis.PrId == null || analysis.PrId == "")
            orderby analysis.CompletedAt descending, ingestion.Revision descending
            select new { Ingestion = ingestion, Analysis = analysis };
        if (effective.Mode is "previous_version" or "number_of_days")
            query = query.Where(x => x.Analysis.Branch == branch)
                .OrderByDescending(x => x.Analysis.CompletedAt).ThenByDescending(x => x.Ingestion.Revision);
        else if (effective.Mode == "reference_branch")
            query = query.Where(x => x.Analysis.Branch == effective.ReferenceBranch)
                .OrderByDescending(x => x.Analysis.CompletedAt).ThenByDescending(x => x.Ingestion.Revision);
        else if (effective.Mode == "specific_analysis")
            query = query.Where(x => x.Analysis.AnalysisId == effective.AnalysisId && x.Analysis.Branch == branch)
                .OrderByDescending(x => x.Analysis.CompletedAt).ThenByDescending(x => x.Ingestion.Revision);
        if (effective.Mode == "number_of_days")
        {
            var cutoff = now.AddDays(-effective.NumberOfDays!.Value);
            query = query.Where(x => x.Analysis.CompletedAt <= cutoff)
                .OrderByDescending(x => x.Analysis.CompletedAt).ThenByDescending(x => x.Ingestion.Revision);
        }

        // ponytail: this is an O(n) manifest scan because project_version is intentionally still
        // manifest-only; promote it to an indexed column if project histories make this measurable.
        await foreach (var candidate in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            var manifest = JsonSerializer.Deserialize<IngestionManifest>(candidate.Ingestion.Manifest, ApiJson.Options);
            if (manifest is null || !RepositoryMatches(manifest.RepositoryUrl, repositoryUrl))
                continue;
            if (effective.Mode == "previous_version"
                && (string.IsNullOrWhiteSpace(manifest.ProjectVersion) || manifest.ProjectVersion == projectVersion))
                continue;
            return new(
                effective,
                source,
                candidate.Ingestion.Id,
                candidate.Analysis.AnalysisId,
                candidate.Analysis.CommitSha,
                candidate.Analysis.Branch,
                manifest.ProjectVersion,
                candidate.Analysis.CompletedAt ?? candidate.Ingestion.CompletedAt ?? candidate.Ingestion.CreatedAt,
                null
            );
        }

        var reason = effective.Mode switch
        {
            "number_of_days" => "number_of_days_baseline_absent",
            "previous_version" => "previous_version_baseline_absent",
            "reference_branch" => "reference_branch_baseline_absent",
            "specific_analysis" => "specific_analysis_baseline_absent",
            _ => "baseline_absent",
        };
        return Empty(effective, source, reason);
    }

    internal static bool RepositoryMatches(string left, string right)
    {
        var leftIdentity = RepositoryUrls.Identity(left);
        var rightIdentity = RepositoryUrls.Identity(right);
        return leftIdentity.Length > 0 && leftIdentity == rightIdentity
            || string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static NewCodeResolution Empty(
        EffectiveNewCodeDefinition effective,
        string source,
        string reason
    ) => new(effective, source, null, null, null, null, null, null, reason);
}
