using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AIVideoEditor.Core;

/// <summary>A user-facing editing error; the message is sent back to the AI as a tool error.</summary>
public sealed class EditException(string message) : Exception(message);

public static class Paths
{
    public static readonly string AppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIVideoEditor");
    public static readonly string Backups = Path.Combine(AppData, "backups");
    public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string Expand(string p) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')));
}

public static class T
{
    public const long US = 1_000_000;
    public static long Us(double seconds) => (long)Math.Round(seconds * US);
    public static double Sec(long us) => Math.Round(us / (double)US, 3);

    public static string Ratio(int w, int h) => (w, h) switch
    {
        (1920, 1080) or (1280, 720) or (3840, 2160) => "16:9",
        (1080, 1920) or (720, 1280) or (2160, 3840) => "9:16",
        (1080, 1080) => "1:1",
        (1440, 1080) => "4:3",
        (1080, 1440) => "3:4",
        (2560, 1080) => "21:9",
        (1080, 1350) => "4:5",
        (2160, 1080) => "2:1",
        _ when w > 0 && h > 0 => Math.Abs((double)w / h - 16.0 / 9.0) < 0.05 ? "16:9"
            : Math.Abs((double)w / h - 9.0 / 16.0) < 0.05 ? "9:16"
            : Math.Abs((double)w / h - 1.0) < 0.03 ? "1:1"
            : Math.Abs((double)w / h - 4.0 / 5.0) < 0.03 ? "4:5"
            : Math.Abs((double)w / h - 4.0 / 3.0) < 0.03 ? "4:3"
            : "16:9",
        _ => "16:9",
    };
}

public static class Ids
{
    /// <summary>CapCut-style upper-case GUID with the 3rd group lower-cased.</summary>
    public static string CapCut()
    {
        var p = Guid.NewGuid().ToString().ToUpperInvariant().Split('-');
        p[2] = p[2].ToLowerInvariant();
        return string.Join('-', p);
    }

    public static string Short() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}

public static class Json
{
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonNode? Load(string path) => JsonNode.Parse(File.ReadAllText(path));

    public static void SaveAtomic(string path, string text)
    {
        var tmp = path + ".aitmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static string Str(this JsonNode? n, string key, string def = "") =>
        n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    public static long Long(this JsonNode? n, string key, long def = 0)
    {
        if (n?[key] is not JsonValue v) return def;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<int>(out var i)) return i;
        return def;
    }

    public static double Dbl(this JsonNode? n, string key, double def = 0)
    {
        if (n?[key] is not JsonValue v) return def;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return def;
    }

    public static bool Bool(this JsonNode? n, string key, bool def = false) =>
        n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    public static JsonArray Arr(this JsonNode n, string key)
    {
        if (n[key] is JsonArray a) return a;
        var na = new JsonArray();
        n[key] = na;
        return na;
    }

    public static JsonNode? From(object? o) => JsonSerializer.SerializeToNode(o, Relaxed);
}

public enum MediaKind { Photo, Video, Audio }

public sealed record MediaInfo(MediaKind Kind, long DurationUs, int Width, int Height, bool HasAudio);

public static class Media
{
    static readonly HashSet<string> Img = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".heic", ".gif", ".tif", ".tiff"];
    static readonly HashSet<string> Vid = [".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm", ".flv", ".mts", ".m2ts", ".3gp"];
    static readonly HashSet<string> Aud = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".opus"];
    public const long PhotoDuration = 10_800_000_000;

    public static MediaKind? KindOf(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        if (Img.Contains(e)) return MediaKind.Photo;
        if (Vid.Contains(e)) return MediaKind.Video;
        if (Aud.Contains(e)) return MediaKind.Audio;
        return null;
    }

    static readonly Dictionary<string, MediaInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads duration and size through the Windows property system (no extra codecs needed).</summary>
    public static MediaInfo Probe(string path)
    {
        if (!File.Exists(path)) throw new EditException($"File not found: {path}");
        var kind = KindOf(path) ?? throw new EditException($"Unsupported file type: {path}");
        lock (Cache) if (Cache.TryGetValue(path, out var c)) return c;
        MediaInfo info;
        try
        {
            info = Task.Run(async () =>
            {
                var f = await StorageFile.GetFileFromPathAsync(path);
                switch (kind)
                {
                    case MediaKind.Photo:
                        var ip = await f.Properties.GetImagePropertiesAsync();
                        int w = (int)ip.Width, h = (int)ip.Height;
                        if (ip.Orientation is PhotoOrientation.Rotate90 or PhotoOrientation.Rotate270
                            or PhotoOrientation.Transpose or PhotoOrientation.Transverse) (w, h) = (h, w);
                        return new MediaInfo(kind, PhotoDuration, w > 0 ? w : 1920, h > 0 ? h : 1080, false);
                    case MediaKind.Video:
                        var vp = await f.Properties.GetVideoPropertiesAsync();
                        int vw = (int)vp.Width, vh = (int)vp.Height;
                        if (vp.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270) (vw, vh) = (vh, vw);
                        return new MediaInfo(kind, (long)vp.Duration.TotalMicroseconds, vw, vh, true);
                    default:
                        var mp = await f.Properties.GetMusicPropertiesAsync();
                        return new MediaInfo(kind, (long)mp.Duration.TotalMicroseconds, 0, 0, true);
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new EditException($"Could not read media info for {path}: {e.Message}");
        }
        if (kind != MediaKind.Photo && info.DurationUs <= 0)
            throw new EditException($"Windows could not read the duration of {Path.GetFileName(path)} (missing codec?)");
        lock (Cache) Cache[path] = info;
        return info;
    }

    public static JsonObject ListFolder(string folder, bool recursive)
    {
        folder = Paths.Expand(folder);
        if (!Directory.Exists(folder)) throw new EditException($"Folder not found: {folder}");
        var res = new Dictionary<string, List<string>> { ["photo"] = [], ["video"] = [], ["audio"] = [] };
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var f in Directory.EnumerateFiles(folder, "*", opt).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var k = KindOf(f);
            if (k != null) res[k.Value.ToString().ToLowerInvariant()].Add(f);
        }
        var o = new JsonObject();
        foreach (var (k, v) in res)
        {
            var list = v.Take(300).ToList();
            if (v.Count > 300) list.Add($"... and {v.Count - 300} more");
            o[k] = Json.From(list);
        }
        o["subfolders"] = Json.From(Directory.GetDirectories(folder).Select(Path.GetFileName).Take(50).ToList());
        return o;
    }

    public static IEnumerable<string> ExpandPaths(IEnumerable<string> paths, bool visualOnly)
    {
        var files = new List<string>();
        foreach (var raw in paths)
        {
            var p = Paths.Expand(raw);
            if (Directory.Exists(p))
                files.AddRange(Directory.GetFiles(p).Where(f => KindOf(f) is { } k && (!visualOnly || k != MediaKind.Audio)));
            else files.Add(p);
        }
        return files;
    }
}

public static class Proc
{
    /// <summary>Processes matching an image name; a trailing '*' matches versioned names (e.g. "vegas*.exe").</summary>
    static Process[] Find(string imageName)
    {
        var name = Path.GetFileNameWithoutExtension(imageName);
        if (!name.EndsWith('*')) return Process.GetProcessesByName(name);
        var prefix = name.TrimEnd('*');
        return Process.GetProcesses().Where(p => p.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static bool Running(string imageName) => Find(imageName).Length > 0;

    public static bool Close(string imageName, int waitMs = 8000)
    {
        foreach (var p in Find(imageName))
            try { p.CloseMainWindow(); } catch { }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs)
        {
            if (Find(imageName).Length == 0) return true;
            Thread.Sleep(400);
        }
        foreach (var p in Find(imageName))
            try { p.Kill(true); } catch { }
        Thread.Sleep(800);
        return Find(imageName).Length == 0;
    }

    public static void Start(string exe, string? args = null)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        Process.Start(psi);
    }
}

public static class Colors
{
    public static double[] Rgb(string color)
    {
        var c = color.Trim().TrimStart('#');
        if (c.Length == 3) c = string.Concat(c.Select(ch => $"{ch}{ch}"));
        if (c.Length != 6) throw new EditException($"Bad color '{color}', use #RRGGBB");
        return [Convert.ToInt32(c[..2], 16) / 255.0, Convert.ToInt32(c[2..4], 16) / 255.0, Convert.ToInt32(c[4..6], 16) / 255.0];
    }

    public static string Hex(string color)
    {
        var r = Rgb(color);
        return "#" + string.Concat(r.Select(v => ((int)Math.Round(v * 255)).ToString("X2")));
    }
}

public static class Srt
{
    public static List<(long StartUs, long DurUs, string Text)> Parse(string srtText)
    {
        var res = new List<(long, long, string)>();
        var blocks = srtText.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            int tsIdx = Array.FindIndex(lines, l => l.Contains("-->"));
            if (tsIdx < 0) continue;
            var parts = lines[tsIdx].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (!TryTime(parts[0], out var t0) || !TryTime(parts[1], out var t1) || t1 <= t0) continue;
            var text = string.Join("\n", lines.Skip(tsIdx + 1)).Trim();
            if (text != "") res.Add((t0, t1 - t0, text));
        }
        return res;
    }

    static bool TryTime(string s, out long us)
    {
        us = 0;
        var norm = s.Trim().Replace(',', '.');
        var parts = norm.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m) ||
            !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec))
            return false;
        us = T.Us(h * 3600 + m * 60 + sec);
        return true;
    }
}
