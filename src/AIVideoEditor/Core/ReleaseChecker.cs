using System.Text.Json.Nodes;

namespace AIVideoEditor.Core;

public sealed record ReleaseInfo(
    string CurrentVersion,
    string LatestVersion,
    bool HasUpdate,
    string Url,
    string Notes,
    string PublishedAt,
    string? SetupUrl,
    string? SetupName,
    long SetupSize);

/// <summary>Checks GitHub Releases for newer builds and downloads verified setup installers.</summary>
public static class ReleaseChecker
{
    public const string ReleasesPageUrl = "https://github.com/dor2500/ai-video-editor/releases";
    public const string LatestApiUrl = "https://api.github.com/repos/dor2500/ai-video-editor/releases/latest";
    const string SetupUrlPrefix = "https://github.com/dor2500/ai-video-editor/releases/download/";

    public static ReleaseInfo ParseReleaseJson(string json, string currentVersion)
    {
        var j = JsonNode.Parse(json) ?? throw new EditException("Invalid release response from GitHub.");
        var latest = j["tag_name"]?.ToString().Trim().TrimStart('v', 'V') ?? "";
        var url = j["html_url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url)) url = ReleasesPageUrl;
        var notes = j["body"]?.ToString() ?? "";
        var published = j["published_at"]?.ToString() ?? "";

        bool hasUpdate = System.Version.TryParse(latest, out var latestVer)
            && System.Version.TryParse(currentVersion, out var curVer)
            && latestVer > curVer;

        string? setupUrl = null;
        string? setupName = null;
        long setupSize = 0;

        if (j["assets"] is JsonArray assets)
        {
            foreach (var asset in assets)
            {
                var name = asset?["name"]?.ToString() ?? "";
                var dl = asset?["browser_download_url"]?.ToString() ?? "";
                if (!name.StartsWith("AIVideoEditor-Setup-", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !dl.StartsWith(SetupUrlPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                setupUrl = dl;
                setupName = Path.GetFileName(name);
                setupSize = asset?["size"]?.GetValue<long>() ?? 0;
                break;
            }
        }

        return new ReleaseInfo(currentVersion, latest, hasUpdate, url, notes, published, setupUrl, setupName, setupSize);
    }

    public static async Task<ReleaseInfo> FetchLatestAsync(string currentVersion, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AIVideoEditor/{currentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(LatestApiUrl, ct);
        return ParseReleaseJson(json, currentVersion);
    }

    public static async Task<string> DownloadInstallerAsync(
        string setupUrl,
        string setupName,
        Action<int, double, double>? onProgress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(setupUrl)
            || !setupUrl.StartsWith(SetupUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new EditException("Release installer URL is missing or points outside the official repository.");
        }

        var cleanName = Path.GetFileName(setupName);
        if (string.IsNullOrWhiteSpace(cleanName) || !cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new EditException("Invalid installer filename.");

        var dir = Path.Combine(Path.GetTempPath(), "AIVideoEditor-Update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, cleanName);
        var tmp = dest + ".tmp";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AIVideoEditor-Updater");

        using var resp = await http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? 0;
        long totalRead = 0;
        var buffer = new byte[64 * 1024];
        var lastReport = Environment.TickCount64 - 200;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            while (true)
            {
                int n = await src.ReadAsync(buffer, ct);
                if (n == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                totalRead += n;

                var now = Environment.TickCount64;
                if (now - lastReport >= 150)
                {
                    lastReport = now;
                    int pct = totalBytes > 0 ? (int)Math.Clamp(totalRead * 100 / totalBytes, 0, 100) : 0;
                    onProgress?.Invoke(pct, totalRead / (1024.0 * 1024.0), totalBytes / (1024.0 * 1024.0));
                }
            }
        }

        File.Move(tmp, dest, true);
        onProgress?.Invoke(100, totalRead / (1024.0 * 1024.0), Math.Max(totalBytes, totalRead) / (1024.0 * 1024.0));
        return dest;
    }
}
