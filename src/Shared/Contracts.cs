// Shared between the server, the updater and the tray app. Plain records, serialized with System.Text.Json (camelCase).
using System.Text.Json;

namespace Syscom.Shared;

public static class SyscomPaths
{
    public const string ServiceName = "SYSCOM";
    public const string AdminHeader = "X-Syscom-Admin";
    public const int DefaultPort = 47810;

    /// <summary>%ProgramData%\SYSCOM (Windows) or ~/.config/SYSCOM elsewhere: config, admin key, update staging, backups.</summary>
    public static string DataDir => Path.Combine(
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "SYSCOM");

    public static string AdminKeyFile => Path.Combine(DataDir, "admin.key");
    public static string InitialPasswordFile => Path.Combine(DataDir, "initial-password.txt");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");
    public static string LastUpdateResult => Path.Combine(UpdatesDir, "last-result.json");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Health>))]
public enum Health { Unknown, Ok, Warning, Error }

public sealed record ComponentStatus(
    string Id,
    string Name,
    string Description,
    string? Installed,
    string? Latest,
    Health Health,
    string HealthDetail,
    bool UpdateAvailable,
    bool CanUpdate,
    bool RequiresRestart,
    string State,            // idle | checking | downloading | installing | restarting | failed
    string? LastError,
    DateTimeOffset? LastChecked);

public sealed record ServerStatus(
    string Name,
    string Version,
    int Port,
    IReadOnlyList<string> Urls,
    int Clients,
    int Sensors,
    bool RunningAsService,
    string? UpdateFeedUrl,
    UpdateResult? LastUpdate);

/// <summary>Instructions for syscom-updater: replace files in the install folder, restart, verify, roll back on failure.</summary>
public sealed record UpdatePlan(
    string InstallDir,
    string ServiceName,
    int? ServerPid,
    string ServerExe,
    IReadOnlyList<string> ServerArgs,
    string HealthUrl,
    string BackupDir,
    string ResultPath,
    IReadOnlyList<UpdateStep> Steps);

/// <summary>Copy everything under <see cref="SourceDir"/> into <c>InstallDir/TargetSubdir</c>.</summary>
public sealed record UpdateStep(string Component, string Version, string SourceDir, string TargetSubdir);

public sealed record UpdateResult(
    DateTimeOffset Time,
    bool Success,
    bool RolledBack,
    IReadOnlyList<string> Components,
    string Message);
