using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;

namespace CodeInsights.VisualStudio.Settings;

#pragma warning disable VSEXTPREVIEW_SETTINGS

internal static class SettingDefinitions
{
    [VisualStudioContribution]
    internal static SettingCategory CodeInsightsCategory { get; } = new("codeinsights", "%CodeInsights.Settings.Category%")
    {
        Description = "%CodeInsights.Settings.Category.Description%",
        GenerateObserverClass = true,
    };

    [VisualStudioContribution]
    internal static Setting.String ServerUrl { get; } = new("serverUrl", "%CodeInsights.Settings.ServerUrl%", CodeInsightsCategory, defaultValue: string.Empty)
    {
        Description = "%CodeInsights.Settings.ServerUrl.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String ProjectKey { get; } = new("projectKey", "%CodeInsights.Settings.ProjectKey%", CodeInsightsCategory, defaultValue: string.Empty)
    {
        Description = "%CodeInsights.Settings.ProjectKey.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Integer RefreshIntervalSeconds { get; } = new("refreshIntervalSeconds", "%CodeInsights.Settings.RefreshIntervalSeconds%", CodeInsightsCategory, defaultValue: 300)
    {
        Description = "%CodeInsights.Settings.RefreshIntervalSeconds.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Boolean DiagnosticsEnabled { get; } = new("diagnosticsEnabled", "%CodeInsights.Settings.DiagnosticsEnabled%", CodeInsightsCategory, defaultValue: true)
    {
        Description = "%CodeInsights.Settings.DiagnosticsEnabled.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Boolean LiveAnalysisEnabled { get; } = new("liveAnalysisEnabled", "%CodeInsights.Settings.LiveAnalysisEnabled%", CodeInsightsCategory, defaultValue: false)
    {
        Description = "%CodeInsights.Settings.LiveAnalysisEnabled.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String TargetBranch { get; } = new("liveAnalysisTargetBranch", "%CodeInsights.Settings.TargetBranch%", CodeInsightsCategory, defaultValue: "origin/master")
    {
        Description = "%CodeInsights.Settings.TargetBranch.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String SemgrepConfig { get; } = new("liveAnalysisSemgrepConfig", "%CodeInsights.Settings.SemgrepConfig%", CodeInsightsCategory, defaultValue: ".codeinsights/semgrep-mqr-v1.json")
    {
        Description = "%CodeInsights.Settings.SemgrepConfig.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Boolean UseDocker { get; } = new("liveAnalysisUseDocker", "%CodeInsights.Settings.UseDocker%", CodeInsightsCategory, defaultValue: false)
    {
        Description = "%CodeInsights.Settings.UseDocker.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String SemgrepImage { get; } = new("liveAnalysisSemgrepImage", "%CodeInsights.Settings.SemgrepImage%", CodeInsightsCategory, defaultValue: "semgrep/semgrep:1.145.0");

    [VisualStudioContribution]
    internal static Setting.String GitleaksImage { get; } = new("liveAnalysisGitleaksImage", "%CodeInsights.Settings.GitleaksImage%", CodeInsightsCategory, defaultValue: "zricethezav/gitleaks:v8.30.0");

    [VisualStudioContribution]
    internal static Setting.Integer TimeoutSeconds { get; } = new("liveAnalysisTimeoutSeconds", "%CodeInsights.Settings.TimeoutSeconds%", CodeInsightsCategory, defaultValue: 120);

    [VisualStudioContribution]
    internal static Setting.Integer MaxFiles { get; } = new("liveAnalysisMaxFiles", "%CodeInsights.Settings.MaxFiles%", CodeInsightsCategory, defaultValue: 100);

    [VisualStudioContribution]
    internal static Setting.Boolean CoverageEnabled { get; } = new("coverageEnabled", "%CodeInsights.Settings.CoverageEnabled%", CodeInsightsCategory, defaultValue: true)
    {
        Description = "%CodeInsights.Settings.CoverageEnabled.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String LocalLcov { get; } = new("coverageLocalLcov", "%CodeInsights.Settings.LocalLcov%", CodeInsightsCategory, defaultValue: string.Empty)
    {
        Description = "%CodeInsights.Settings.LocalLcov.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Boolean DuplicationEnabled { get; } = new("duplicationEnabled", "%CodeInsights.Settings.DuplicationEnabled%", CodeInsightsCategory, defaultValue: true);

    [VisualStudioContribution]
    internal static Setting.Boolean DependenciesEnabled { get; } = new("dependenciesEnabled", "%CodeInsights.Settings.DependenciesEnabled%", CodeInsightsCategory, defaultValue: true);
}

#pragma warning restore VSEXTPREVIEW_SETTINGS
