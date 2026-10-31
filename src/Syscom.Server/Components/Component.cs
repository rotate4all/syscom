using System.Security.Cryptography;
using Syscom.Shared;

namespace Syscom.Server.Components;

/// <summary>What an update check found: the newest version and where to get it.</summary>
public sealed record LatestRelease(string Version, string? Url = null, string? Sha256 = null, string? Sha512 = null, object? Extra = null);

/// <summary>Result of preparing an update: files for the updater to copy, or work already done in-process.</summary>
public sealed record StagedUpdate(IReadOnlyList<UpdateStep> Steps, bool NeedsRestart);

/// <summary>One separately updatable part of SYSCOM.</summary>
public abstract class Component
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>Installed version, or null when the component is missing.</summary>
    public abstract string? InstalledVersion();

    public abstract (Health Health, string Detail) CheckHealth();

    /// <summary>Whether this machine/installation supports updating the component from the app.</summary>
    public virtual (bool CanUpdate, string? Reason) UpdateSupport() => (true, null);

    /// <summary>Asks the component's official source for its newest release. Null = source not configured.</summary>
    public abstract Task<LatestRelease?> GetLatestAsync(HttpClient http, CancellationToken ct);

    /// <summary>Downloads and verifies the release into <paramref name="staging"/>; may also apply it directly.</summary>
    public abstract Task<StagedUpdate> StageAsync(LatestRelease release, string staging, HttpClient http, CancellationToken ct);

    /// <summary>Called once at startup, e.g. to clean up after a finished update.</summary>
    public virtual void OnStartup() { }

    protected static string AppDir => AppContext.BaseDirectory;

    // ---- helpers shared by components

    protected static async Task<string> DownloadAsync(HttpClient http, string url, string targetFile, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using (var fs = File.Create(targetFile))
        {
            await resp.Content.CopyToAsync(fs, ct);
        }
        return targetFile;
    }

    protected static void VerifyHash(string file, string? sha256Hex, string? sha512Hex)
    {
        if (string.IsNullOrWhiteSpace(sha256Hex) && string.IsNullOrWhiteSpace(sha512Hex))
            throw new InvalidOperationException($"No checksum published for {Path.GetFileName(file)}; refusing to install it.");

        using var fs = File.OpenRead(file);
        var (actual, expected) = !string.IsNullOrWhiteSpace(sha512Hex)
            ? (Convert.ToHexString(SHA512.HashData(fs)), sha512Hex)
            : (Convert.ToHexString(SHA256.HashData(fs)), sha256Hex!);
        if (!actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(file)}.");
    }
}

public static class VersionUtil
{
    /// <summary>Parses "1.2.3", "v1.2.3", "1.2.3-pre45+abc" into a comparable version (prerelease suffix ignored).</summary>
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        var parts = s.Split('.');
        if (parts.Length == 1) s += ".0";
        if (!Version.TryParse(s, out var v)) return null;
        // Normalize so 0.9.6 == 0.9.6.0.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    public static bool IsNewer(string? candidate, string? installed)
    {
        var c = Parse(candidate);
        var i = Parse(installed);
        if (c is null) return false;
        return i is null || c > i;
    }

    public static bool IsPrerelease(string s) => s.Contains('-');
}
