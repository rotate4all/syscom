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
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/api/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    public int Port { get; }
    public string DashboardUrl => $"http://localhost:{Port}/";

    private void EnsureKey()
    {
        if (_http.DefaultRequestHeaders.Contains(SyscomPaths.AdminHeader)) return;
        if (!File.Exists(SyscomPaths.AdminKeyFile)) throw new InvalidOperationException("SYSCOM has not started yet (no admin key).");
        _http.DefaultRequestHeaders.Add(SyscomPaths.AdminHeader, File.ReadAllText(SyscomPaths.AdminKeyFile).Trim());
    }

    public async Task<ServerStatus?> StatusAsync(CancellationToken ct = default)
    {
        EnsureKey();
        return await _http.GetFromJsonAsync<ServerStatus>("admin/status", SyscomPaths.Json, ct);
    }

    public async Task<List<ComponentStatus>> ComponentsAsync(CancellationToken ct = default)
    {
        EnsureKey();
        return await _http.GetFromJsonAsync<List<ComponentStatus>>("admin/components", SyscomPaths.Json, ct) ?? new();
    }

    public async Task<List<ComponentStatus>> CheckAsync(CancellationToken ct = default)
    {
        EnsureKey();
        using var resp = await _http.PostAsync("admin/components/check", null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<ComponentStatus>>(SyscomPaths.Json, ct) ?? new();
    }

    public Task<string> UpdateAsync(IEnumerable<string> ids) => PostMessage("admin/components/update", new { ids = ids.ToArray() });
    public Task<string> RestartAsync() => PostMessage("admin/restart", new { });
    public Task<string> SetPasswordAsync(string password) => PostMessage("admin/password", new { password });

    private async Task<string> PostMessage(string path, object body)
    {
        EnsureKey();
        using var resp = await _http.PostAsJsonAsync(path, body, SyscomPaths.Json);
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
