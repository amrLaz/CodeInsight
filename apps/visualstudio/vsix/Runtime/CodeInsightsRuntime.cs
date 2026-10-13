using System.Diagnostics;
using CodeInsights.VisualStudio.Auth;
using CodeInsights.VisualStudio.Host;
using CodeInsights.VisualStudio.Settings;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Helpers;
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
#pragma warning disable VSEXTPREVIEW_SETTINGS

namespace CodeInsights.VisualStudio.Runtime;

internal sealed class CodeInsightsRuntime : DisposableObject
{
    private readonly VisualStudioExtensibility _extensibility;
    private readonly HostClient _host;
    private readonly TokenStore _tokens;
    private readonly CodeInsightsCategoryObserver _settings;
    private readonly TraceSource _trace;
    private OutputChannel? _output;
    private Timer? _timer;
    private string _cwd = "";
    private Snapshot? _snapshot;
    private HostSettingsDto _lastSettings = new();

    public CodeInsightsRuntime(
        VisualStudioExtensibility extensibility,
        HostClient host,
        TokenStore tokens,
        CodeInsightsCategoryObserver settings,
        TraceSource traceSource)
    {
        _extensibility = extensibility;
        _host = host;
        _tokens = tokens;
        _settings = settings;
        _trace = traceSource;
        _host.Logged += message => _ = WriteAsync(message);
        _host.Unauthorized += () => _ = WriteAsync("Jeton refusé par le hub : absent, expiré ou révoqué.");
        _settings.Changed += SettingsChangedAsync;
    }

    public Snapshot? LastSnapshot => _snapshot;

    public HostSettingsDto LastSettings => _lastSettings;

    public event Action<Snapshot>? SnapshotChanged;

    public async Task<Snapshot> EnsureReadyAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        var cwd = await ResolveCwdAsync(context, cancellationToken);
        if (!string.Equals(cwd, _cwd, StringComparison.OrdinalIgnoreCase))
        {
            _cwd = cwd;
            _snapshot = await _host.SetCwdAsync(cwd, cancellationToken);
            SnapshotChanged?.Invoke(_snapshot);
        }

        return _snapshot ?? await RefreshAsync(cancellationToken);
    }

    public async Task<Snapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        _snapshot = await _host.RefreshAsync(cancellationToken);
        SnapshotChanged?.Invoke(_snapshot);
        await AnnounceAsync(_snapshot, cancellationToken);
        Schedule(_snapshot);
        return _snapshot;
    }

    public Task<Snapshot> SignInAsync(string token, CancellationToken cancellationToken)
    {
        _tokens.Set(token);
        return ApplyTokenAsync(token, cancellationToken);
    }

    public Task<Snapshot> SignOutAsync(CancellationToken cancellationToken)
    {
        _tokens.Clear();
        return ApplyTokenAsync(null, cancellationToken);
    }

    public Task<Snapshot> AnalyzeAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => _host.AnalyzeChangesAsync(cancellationToken), cancellationToken);

    public Task<Snapshot> FilterIssuesAsync(object filters, CancellationToken cancellationToken) =>
        InvokeAsync(() => _host.FilterIssuesAsync(filters, cancellationToken), cancellationToken);

    public Task<Snapshot> ClearIssueFiltersAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => _host.ClearIssueFiltersAsync(cancellationToken), cancellationToken);

    public Task<Snapshot> FilterRulesAsync(object filters, CancellationToken cancellationToken) =>
        InvokeAsync(() => _host.FilterRulesAsync(filters, cancellationToken), cancellationToken);

    public Task<Snapshot> SelectIssueAsync(int issueId, CancellationToken cancellationToken) =>
        InvokeAsync(() => _host.SelectIssueAsync(issueId, cancellationToken), cancellationToken);

    public Task<Snapshot> TriageAsync(object body, CancellationToken cancellationToken) =>
        InvokeAsync(() => _host.TriageAsync(body, cancellationToken), cancellationToken);

    public async Task<PromptResultDto> PromptAsync(int issueId, CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        return await _host.PromptAsync(issueId, cancellationToken);
    }

    public async Task<FileContextDto> FileContextAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        return await _host.FileContextAsync(path, cancellationToken);
    }

    public async Task<string> HubUrlAsync(string kind, object? id, CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        return await _host.HubUrlAsync(kind, id, cancellationToken);
    }

    public async Task<string> InstallHookAsync(CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        return await _host.InstallHookAsync(cancellationToken);
    }

    public async Task OpenPathAsync(string relativeOrAbsolute, int? line, CancellationToken cancellationToken)
    {
        var full = Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.GetFullPath(Path.Combine(_cwd, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(full)) throw new FileNotFoundException($"Fichier introuvable : {relativeOrAbsolute}", full);
        await _extensibility.Documents().OpenTextDocumentAsync(new Uri(full), cancellationToken);
        _ = line;
    }

    public string HostDirectory
    {
        get
        {
            var assembly = Path.GetDirectoryName(typeof(CodeInsightsExtension).Assembly.Location) ?? "";
            return assembly;
        }
    }

    public string? McpServerPath
    {
        get
        {
            var candidate = Path.Combine(HostDirectory, "mcp-server.mjs");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            _timer?.Dispose();
            _output?.Dispose();
            _settings.Changed -= SettingsChangedAsync;
        }

        base.Dispose(isDisposing);
    }

    private async Task SettingsChangedAsync(CodeInsightsCategorySnapshot snapshot)
    {
        try
        {
            await EnsureHostAsync(CancellationToken.None);
            _lastSettings = ToDto(snapshot);
            _snapshot = await _host.SetSettingsAsync(_lastSettings, CancellationToken.None);
            SnapshotChanged?.Invoke(_snapshot);
            Schedule(_snapshot);
        }
        catch (Exception exception)
        {
            _trace.TraceEvent(TraceEventType.Error, 0, exception.ToString());
        }
    }

    private async Task EnsureHostAsync(CancellationToken cancellationToken)
    {
        _output ??= await _extensibility.Views().Output.CreateOutputChannelAsync("CodeInsights", cancellationToken);
        var hostPath = Path.Combine(HostDirectory, "host.mjs");
        await _host.EnsureStartedAsync(hostPath, cancellationToken);
        if (_snapshot is not null) return;

        var settings = ToDto(await _settings.GetSnapshotAsync(cancellationToken));
        _lastSettings = settings;
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeInsights",
            "visualstudio",
            "cache");
        Directory.CreateDirectory(cacheDir);
        _snapshot = await _host.InitializeAsync(
            new InitializeRequest
            {
                Cwd = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                CacheDir = cacheDir,
                Token = _tokens.Get(),
                Settings = settings,
            },
            cancellationToken);
        SnapshotChanged?.Invoke(_snapshot);
        Schedule(_snapshot);
    }

    private async Task<Snapshot> ApplyTokenAsync(string? token, CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        _snapshot = await _host.SetTokenAsync(token, cancellationToken);
        SnapshotChanged?.Invoke(_snapshot);
        await AnnounceAsync(_snapshot, cancellationToken);
        return _snapshot;
    }

    private async Task<Snapshot> InvokeAsync(Func<Task<Snapshot>> action, CancellationToken cancellationToken)
    {
        await EnsureHostAsync(cancellationToken);
        _snapshot = await action();
        SnapshotChanged?.Invoke(_snapshot);
        await AnnounceAsync(_snapshot, cancellationToken);
        return _snapshot;
    }

    private async Task<string> ResolveCwdAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            var selected = await context.GetSelectedPathAsync(cancellationToken);
            if (selected is not null)
            {
                var local = selected.LocalPath;
                return selected.IsFile ? Path.GetDirectoryName(local) ?? local : local;
            }
        }
        catch (InvalidOperationException)
        {
            // No selection yet: fall through to the active document.
        }

        try
        {
            var view = await context.GetActiveTextViewAsync(cancellationToken);
            if (view is not null && view.Uri.IsFile) return Path.GetDirectoryName(view.Uri.LocalPath) ?? _cwd;
        }
        catch (InvalidOperationException)
        {
        }

        return string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd;
    }

    private void Schedule(Snapshot snapshot)
    {
        _ = snapshot;
        _timer?.Dispose();
        var seconds = _lastSettings.RefreshIntervalSeconds;
        if (seconds <= 0) return;
        var period = TimeSpan.FromSeconds(Math.Max(60, seconds));
        _timer = new Timer(_ => _ = SafeRefresh(), null, period, period);
    }

    private async Task SafeRefresh()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0, exception.ToString());
        }
    }

    private async Task AnnounceAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(snapshot.Notice)) return;
        await WriteAsync(snapshot.Notice);
        await _extensibility.Shell().ShowPromptAsync(snapshot.Notice, Microsoft.VisualStudio.Extensibility.Shell.PromptOptions.OK, cancellationToken);
    }

    private async Task WriteAsync(string message)
    {
        if (_output is null) return;
        await _output.WriteLineAsync(message);
    }

    internal static HostSettingsDto ToDto(CodeInsightsCategorySnapshot snapshot)
    {
        var semgrep = snapshot.SemgrepConfig.ValueOrDefault(".codeinsights/semgrep-mqr-v1.json");
        return new HostSettingsDto
        {
            ServerUrl = snapshot.ServerUrl.ValueOrDefault(string.Empty).Trim(),
            ProjectKey = snapshot.ProjectKey.ValueOrDefault(string.Empty).Trim(),
            RefreshIntervalSeconds = snapshot.RefreshIntervalSeconds.ValueOrDefault(300),
            DiagnosticsEnabled = snapshot.DiagnosticsEnabled.ValueOrDefault(true),
            LiveAnalysisEnabled = snapshot.LiveAnalysisEnabled.ValueOrDefault(false),
            LiveAnalysisTargetBranch = snapshot.TargetBranch.ValueOrDefault("origin/master"),
            LiveAnalysisSemgrepConfig = semgrep
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LiveAnalysisUseDocker = snapshot.UseDocker.ValueOrDefault(false),
            LiveAnalysisImages = new HostImagesDto
            {
                Semgrep = snapshot.SemgrepImage.ValueOrDefault("semgrep/semgrep:1.145.0"),
                Gitleaks = snapshot.GitleaksImage.ValueOrDefault("zricethezav/gitleaks:v8.30.0"),
            },
            LiveAnalysisTimeoutSeconds = snapshot.TimeoutSeconds.ValueOrDefault(120),
            LiveAnalysisMaxFiles = snapshot.MaxFiles.ValueOrDefault(100),
            CoverageEnabled = snapshot.CoverageEnabled.ValueOrDefault(true),
            CoverageLocalLcov = snapshot.LocalLcov.ValueOrDefault(string.Empty),
            DuplicationEnabled = snapshot.DuplicationEnabled.ValueOrDefault(true),
            DependenciesEnabled = snapshot.DependenciesEnabled.ValueOrDefault(true),
        };
    }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
#pragma warning restore VSEXTPREVIEW_SETTINGS
