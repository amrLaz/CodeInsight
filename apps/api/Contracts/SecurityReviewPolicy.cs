namespace CodeInsights.Api.Contracts;

public sealed partial record ProfileDefinition
{
    public SecurityReviewPolicy SecurityReview { get; init; } = new();
}

/// <summary>Immutable profile policy. Existing profiles keep human reviews informative.</summary>
public sealed record SecurityReviewPolicy(bool ApplyApprovedExceptions = false, double? MinimumHotspotReviewPercent = null, string Scope = "global")
{
    public void Validate()
    {
        if (Scope is not ("global" or "new_code") || MinimumHotspotReviewPercent is { } threshold && (!double.IsFinite(threshold) || threshold < 0 || threshold > 100))
            throw new ApiException(422, "Invalid security review scope or percentage");
    }
}
