using System.Linq.Expressions;
using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

/// <summary>
/// A branch build owns its branch's verdict, history, trends and New Code baselines.
/// A pull request build only describes its pull request: it is stored under its source
/// branch but never stands in for that branch.
/// </summary>
public static class AnalysisKinds
{
    public static readonly Expression<Func<AnalysisRecord, bool>> Branch = x => x.PrId == null || x.PrId == "";
    public static readonly Expression<Func<AnalysisRecord, bool>> PullRequest = x => x.PrId != null && x.PrId != "";
}
