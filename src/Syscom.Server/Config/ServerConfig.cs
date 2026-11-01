using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syscom.Server.Config;

/// <summary>Persistent server settings, stored as JSON.</summary>
public sealed class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.MachineName;
    public int Port { get; set; } = 47810;

    /// <summary>Also serve the dashboard over HTTPS with this PC's own certificate (see /trust).</summary>
    public bool HttpsEnabled { get; set; } = true;
    public int HttpsPort { get; set; } = 47812;
    public bool DiscoveryEnabled { get; set; } = true;
    public int DiscoveryPort { get; set; } = 47811;

    /// <summary>Record every sensor for 30 days (shown when a dashboard card is tapped). Keeps sensors polled slowly
    /// while no browser is connected.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>How often sensors are read for the history while no browser is connected, in milliseconds.</summary>
    public int HistorySampleMs { get; set; } = 2000;

    /// <summary>Lowest polling interval a client may request, in milliseconds.</summary>
    public int MinIntervalMs { get; set; } = 500;
    public int TokenLifetimeDays { get; set; } = 30;

    /// <summary>Where new SYSCOM app / web client releases are announced (feed.json, see docs/UPDATES.md).</summary>
    public string? UpdateFeedUrl { get; set; } = "https://github.com/rotate4all/syscom/releases/latest/download/feed.json";

    /// <summary>Check every component for updates every <see cref="UpdateCheckHours"/> hours.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>How often the update check runs, in hours (1 to 168).</summary>
    public int UpdateCheckHours { get; set; } = 6;

    /// <summary>
    /// What a check installs by itself: "syscom" (SYSCOM and its web dashboard, the default), "all" (every component,
    /// including LibreHardwareMonitor, PawnIO and the .NET runtime) or "off" (only report, install from the tray app).
    /// </summary>
    public string AutoUpdate { get; set; } = "syscom";

    /// <summary>Arguments for a silent PawnIO_setup.exe install/upgrade.</summary>
    public string PawnIoInstallArgs { get; set; } = "-install -silent";

    /// <summary>Oldest PawnIO driver version accepted without a startup warning (older builds had security issues).</summary>
    public string MinPawnIoVersion { get; set; } = "2.2.0";

    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public string? TokenSecret { get; set; }

    [JsonIgnore] public string FilePath { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(Syscom.Shared.SyscomPaths.DataDir, "config.json");

    public static ServerConfig Load(string path)
    {
        ServerConfig cfg;
        if (File.Exists(path))
        {
            cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOptions) ?? new ServerConfig();
        }
        else
        {
            cfg = new ServerConfig();
        }
        cfg.FilePath = path;
        if (cfg.MinIntervalMs < 100) cfg.MinIntervalMs = 100;
        cfg.UpdateCheckHours = Math.Clamp(cfg.UpdateCheckHours, 1, 168);
        if (cfg.AutoUpdate is not ("syscom" or "all" or "off")) cfg.AutoUpdate = "syscom";
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
