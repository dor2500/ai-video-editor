using System.Diagnostics;
using System.Text;

namespace AIVideoEditor.Core;

public sealed record DownloadResult(
    string Path,
    string Title,
    string Kind,
    double DurationS,
    int Width,
    int Height,
    long SizeBytes)
{
    public object ToDto() => new
    {
        path = Path,
        name = System.IO.Path.GetFileName(Path),
        title = Title,
        kind = Kind,
        duration_s = DurationS,
        width = Width,
        height = Height,
        size_bytes = SizeBytes,
    };
}

/// <summary>Downloads audio or video from YouTube via yt-dlp into the local Downloads folder.</summary>
public static class YouTubeDownloader
{
    const string YtDlpReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    static readonly SemaphoreSlim YtDlpLock = new(1, 1);

    public static string DownloadsDir => System.IO.Path.Combine(Paths.Documents, "AI Video Editor", "Downloads");
    public static string ToolsDir => System.IO.Path.Combine(Paths.LocalAppData, "AIVideoEditor", "tools");

    public static string NormalizeTarget(string rawInput)
    {
        var trimmed = (rawInput ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new EditException("Provide a YouTube link or song search query.");
        if (trimmed.StartsWith('-'))
            throw new EditException("YouTube input cannot start with '-'.");

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new EditException("Invalid URL format.");
            }

            var host = uri.Host.ToLowerInvariant();
            bool isYouTube = host == "youtube.com" || host == "youtu.be"
                || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                || host.EndsWith(".youtu.be", StringComparison.Ordinal);
            if (!isYouTube)
                throw new EditException("Only YouTube URLs (youtube.com / youtu.be) or song search queries are supported.");

            return trimmed;
        }

        return "ytsearch1:" + trimmed;
    }

    static string? FindOnPath(string exeName)
    {
        var toolCandidate = System.IO.Path.Combine(ToolsDir, exeName);
        if (File.Exists(toolCandidate)) return toolCandidate;

        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in envPath.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    static async Task<string> EnsureYtDlpAsync(Action<string>? onStatus, CancellationToken ct)
    {
        if (FindOnPath("yt-dlp.exe") is { } existing) return existing;

        await YtDlpLock.WaitAsync(ct);
        try
        {
            if (FindOnPath("yt-dlp.exe") is { } secondCheck) return secondCheck;

            Directory.CreateDirectory(ToolsDir);
            var dest = System.IO.Path.Combine(ToolsDir, "yt-dlp.exe");
            var tmp = dest + ".tmp";

            onStatus?.Invoke("Downloading yt-dlp helper (one-time setup)...");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AIVideoEditor");
            using var resp = await http.GetAsync(YtDlpReleaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await src.CopyToAsync(dst, ct);
            }

            File.Move(tmp, dest, true);
            return dest;
        }
        finally
        {
            YtDlpLock.Release();
        }
    }

    public static async Task<DownloadResult> DownloadAsync(
        string input,
        string mode,
        Action<string>? onStatus,
        CancellationToken ct = default)
    {
        var target = NormalizeTarget(input);
        bool isVideo = string.Equals(mode, "video", StringComparison.OrdinalIgnoreCase);

        var ytDlp = await EnsureYtDlpAsync(onStatus, ct);
        bool hasFfmpeg = FindOnPath("ffmpeg.exe") != null;

        Directory.CreateDirectory(DownloadsDir);
        onStatus?.Invoke($"Downloading {(isVideo ? "video" : "audio")} from YouTube...");

        var psi = new ProcessStartInfo(ytDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = DownloadsDir,
        };

        var args = psi.ArgumentList;
        args.Add("--no-playlist");
        args.Add("--no-part");
        args.Add("--windows-filenames");
        args.Add("--encoding");
        args.Add("utf-8");
        args.Add("--no-progress");
        args.Add("--print");
        args.Add("after_move:filepath");
        args.Add("-o");
        args.Add(System.IO.Path.Combine(DownloadsDir, "%(title).70B [%(id)s].%(ext)s"));

        if (isVideo)
        {
            if (hasFfmpeg)
            {
                args.Add("-f");
                args.Add("bestvideo[ext=mp4][height<=1080]+bestaudio[ext=m4a]/best[ext=mp4]/best");
                args.Add("--merge-output-format");
                args.Add("mp4");
            }
            else
            {
                args.Add("-f");
                args.Add("best[ext=mp4][vcodec!=none][acodec!=none]/best");
            }
        }
        else
        {
            if (hasFfmpeg)
            {
                args.Add("-f");
                args.Add("bestaudio/best");
                args.Add("-x");
                args.Add("--audio-format");
                args.Add("mp3");
                args.Add("--audio-quality");
                args.Add("0");
            }
            else
            {
                args.Add("-f");
                args.Add("bestaudio[ext=m4a]/bestaudio[ext=mp3]/bestaudio");
            }
        }

        args.Add("--");
        args.Add(target);

        using var proc = Process.Start(psi) ?? throw new EditException("Failed to launch yt-dlp.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new EditException("YouTube download timed out after 5 minutes.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            var errLines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var errMsg = errLines.LastOrDefault(l => l.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                ?? errLines.LastOrDefault()
                ?? $"yt-dlp failed with exit code {proc.ExitCode}.";
            throw new EditException(errMsg);
        }

        var outLines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filePath = outLines.LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new EditException("YouTube download finished but the output file was not found.");

        var fullPath = System.IO.Path.GetFullPath(filePath);
        double durationS = 0;
        int width = 0, height = 0;
        string kind = isVideo ? "video" : "audio";
        try
        {
            var probe = Media.Probe(fullPath);
            kind = probe.Kind.ToString().ToLowerInvariant();
            durationS = T.Sec(probe.DurationUs);
            width = probe.Width;
            height = probe.Height;
        }
        catch
        {
            // Keep graceful fallback metadata if Windows property reader lacks a container handler.
        }

        long sizeBytes = new FileInfo(fullPath).Length;
        var title = System.IO.Path.GetFileNameWithoutExtension(fullPath);
        return new DownloadResult(fullPath, title, kind, durationS, width, height, sizeBytes);
    }
}
