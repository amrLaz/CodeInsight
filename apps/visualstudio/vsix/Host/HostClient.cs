using System.Diagnostics;
using System.Text.Json;
using StreamJsonRpc;

namespace CodeInsights.VisualStudio.Host;

/// <summary>
/// One Node host process speaking JSON-RPC 2.0 with Content-Length framing.
/// Visual Studio never reimplements a gate, a drift mapping or a local estimate.
/// </summary>
internal sealed class HostClient : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private JsonRpc? _rpc;
    private bool _disposed;

    public event Action<string>? Logged;
    public event Action? Unauthorized;

    public bool IsRunning => _rpc is not null && _process is { HasExited: false };

    public async Task EnsureStartedAsync(string hostPath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (IsRunning) return;
        }

        var node = FindNode() ?? throw new InvalidOperationException(
            "Node.js 22 ou plus récent est requis dans le PATH. Visual Studio n'embarque pas de runtime Node.");
        if (!File.Exists(hostPath))
            throw new InvalidOperationException($"Hôte CodeInsights introuvable : {hostPath}");

        var start = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{hostPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory,
        };

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Impossible de démarrer l'hôte CodeInsights.");
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data)) Logged?.Invoke(args.Data);
        };
        process.BeginErrorReadLine();

        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        var handler = new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(this);
        rpc.StartListening();

        lock (_gate)
        {
            _process = process;
            _rpc = rpc;
        }

        await Task.CompletedTask.WaitAsync(cancellationToken);
    }

    [JsonRpcMethod("logInfo")]
    public void OnLogInfo(LogMessage message) => Logged?.Invoke(message.Message);

    [JsonRpcMethod("logWarn")]
    public void OnLogWarn(LogMessage message) => Logged?.Invoke(message.Message);

    [JsonRpcMethod("unauthorized")]
    public void OnUnauthorized() => Unauthorized?.Invoke();

    public Task<Snapshot> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("initialize", request, cancellationToken);

    public Task<Snapshot> SetSettingsAsync(HostSettingsDto settings, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("setSettings", settings, cancellationToken);

    public Task<Snapshot> SetTokenAsync(string? token, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("setToken", new { token }, cancellationToken);

    public Task<Snapshot> SetCwdAsync(string cwd, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("setCwd", new { cwd }, cancellationToken);

    public Task<Snapshot> RefreshAsync(CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("refresh", new { }, cancellationToken);

    public Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("getSnapshot", new { }, cancellationToken);

    public Task<Snapshot> FilterIssuesAsync(object filters, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("filterIssues", filters, cancellationToken);

    public Task<Snapshot> ClearIssueFiltersAsync(CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("clearIssueFilters", new { }, cancellationToken);

    public Task<Snapshot> FilterRulesAsync(object filters, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("filterRules", filters, cancellationToken);

    public Task<Snapshot> SelectIssueAsync(int issueId, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("selectIssue", new { issueId }, cancellationToken);

    public Task<Snapshot> AnalyzeChangesAsync(CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("analyzeChanges", new { }, cancellationToken);

    public Task<Snapshot> TriageAsync(object body, CancellationToken cancellationToken) =>
        InvokeAsync<Snapshot>("triage", body, cancellationToken);

    public Task<PromptResultDto> PromptAsync(int issueId, CancellationToken cancellationToken) =>
        InvokeAsync<PromptResultDto>("prompt", new { issueId }, cancellationToken);

    public Task<FileContextDto> FileContextAsync(string path, CancellationToken cancellationToken) =>
        InvokeAsync<FileContextDto>("fileContext", new { path }, cancellationToken);

    public Task<string> HubUrlAsync(string kind, object? id, CancellationToken cancellationToken) =>
        InvokeAsync<string>("hubUrl", new { kind, id }, cancellationToken);

    public Task<string> InstallHookAsync(CancellationToken cancellationToken) =>
        InvokeAsync<string>("installHook", new { }, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _rpc?.Dispose(); } catch { }
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch { }
        _process?.Dispose();
    }

    private async Task<T> InvokeAsync<T>(string method, object argument, CancellationToken cancellationToken)
    {
        var rpc = _rpc ?? throw new InvalidOperationException("Hôte CodeInsights non démarré.");
        return await rpc.InvokeWithParameterObjectAsync<T>(method, argument, cancellationToken).ConfigureAwait(false);
    }

    private static string? FindNode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node" } : new[] { "node" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var extra in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                 })
        {
            if (File.Exists(extra)) return extra;
        }

        return null;
    }

    internal sealed class LogMessage
    {
        public string Message { get; set; } = "";
    }
}
