using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Syscom.Shared;

namespace Syscom.Tray;

/// <summary>Talks to the local SYSCOM service's management API (loopback + admin key).</summary>
public sealed class AdminClient : IDisposable
{
    private readonly HttpClient _http;

    public AdminClient()
    {
        Port = ReadPort();
        // Each call sets its own time limit: status calls are quick, an update downloads the new files first.
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/api/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public int Port { get; }
    public string DashboardUrl => $"http://localhost:{Port}/";

    private void EnsureKey()
    {
        if (_http.DefaultRequestHeaders.Contains(SyscomPaths.AdminHeader)) return;
        if (!File.Exists(SyscomPaths.AdminKeyFile)) throw new InvalidOperationException("SYSCOM has not started yet (no admin key).");
        _http.DefaultRequestHeaders.Add(SyscomPaths.AdminHeader, File.ReadAllText(SyscomPaths.AdminKeyFile).Trim());
    }

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(15);

    public async Task<ServerStatus?> StatusAsync()
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(Quick);
        return await _http.GetFromJsonAsync<ServerStatus>("admin/status", SyscomPaths.Json, cts.Token);
    }

    public async Task<List<ComponentStatus>> ComponentsAsync()
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(Quick);
        return await _http.GetFromJsonAsync<List<ComponentStatus>>("admin/components", SyscomPaths.Json, cts.Token) ?? new();
    }

    public async Task<List<ComponentStatus>> CheckAsync()
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var resp = await _http.PostAsync("admin/components/check", null, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<ComponentStatus>>(SyscomPaths.Json, cts.Token) ?? new();
    }

    public async Task<ServerSettings?> SettingsAsync()
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(Quick);
        return await _http.GetFromJsonAsync<ServerSettings>("admin/settings", SyscomPaths.Json, cts.Token);
    }

    public async Task<ServerSettings?> SaveSettingsAsync(ServerSettings settings)
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(Quick);
        using var resp = await _http.PostAsJsonAsync("admin/settings", settings, SyscomPaths.Json, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ServerSettings>(SyscomPaths.Json, cts.Token);
    }

    /// <summary>Downloads and installs updates (the .NET runtime alone is tens of MB, so this may take minutes).</summary>
    public Task<UpdateOutcome> UpdateAsync(IEnumerable<string> ids) =>
        PostOutcome("admin/components/update", new { ids = ids.ToArray() }, TimeSpan.FromMinutes(20));
    public Task<UpdateOutcome> RestartAsync() => PostOutcome("admin/restart", new { }, Quick);
    public Task<string> SetPasswordAsync(string password) => PostMessage("admin/password", new { password });

    private async Task<UpdateOutcome> PostOutcome(string path, object body, TimeSpan timeout)
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(timeout);
        using var resp = await _http.PostAsJsonAsync(path, body, SyscomPaths.Json, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<UpdateOutcome>(SyscomPaths.Json, cts.Token) ?? new UpdateOutcome("", false);
    }

    private async Task<string> PostMessage(string path, object body)
    {
        EnsureKey();
        using var cts = new CancellationTokenSource(Quick);
        using var resp = await _http.PostAsJsonAsync(path, body, SyscomPaths.Json, cts.Token);
        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? "";
        }
        catch (JsonException) { }
        resp.EnsureSuccessStatusCode();
        return "";
    }

    public static string? InitialPassword() =>
        File.Exists(SyscomPaths.InitialPasswordFile) ? File.ReadAllText(SyscomPaths.InitialPasswordFile).Trim() : null;

    /// <summary>Service state as reported by the SCM ("RUNNING", "STOPPED", …) or null if not installed.</summary>
    public static string? ServiceState()
    {
        var output = Sc("query");
        if (output.Contains("1060")) return null; // ERROR_SERVICE_DOES_NOT_EXIST
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("STATE")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1].Trim() : null;
        }
        return null;
    }

    public static void StartService() => Sc("start");

    private static string Sc(string verb)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", $"{verb} {SyscomPaths.ServiceName}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return s;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static int ReadPort()
    {
        try
        {
            var file = Path.Combine(SyscomPaths.DataDir, "config.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("Port", out var p) && p.TryGetInt32(out var port)) return port;
        }
        catch (Exception) { }
        return SyscomPaths.DefaultPort;
    }

    public void Dispose() => _http.Dispose();
}
