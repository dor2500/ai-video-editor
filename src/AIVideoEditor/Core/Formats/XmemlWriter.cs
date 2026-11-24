using System.Globalization;
using System.Xml.Linq;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core.Formats;

static class ExportUtil
{
    public static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string FileUrl(string path, bool localhost)
    {
        var p = Path.GetFullPath(path).Replace('\\', '/');
        var enc = string.Join('/', p.Split('/').Select((s, i) => i == 0 && s.EndsWith(':') ? s : Uri.EscapeDataString(s)));
        return (localhost ? "file://localhost/" : "file:///") + enc;
    }

    /// <summary>Scale that makes media fill the canvas the way "fit" does in the editors.</summary>
    public static double Fit(TimelineDoc d, TClip c) =>
        c.MediaWidth > 0 && c.MediaHeight > 0 ? Math.Min((double)d.Width / c.MediaWidth, (double)d.Height / c.MediaHeight) : 1;

    public static (double s0, double x0, double y0, double s1, double x1, double y1) Motion(TClip c)
    {
        double z = 1 + c.MotionStrength, h = c.MotionStrength / 2, s = c.Scale;
        return c.Motion switch
        {
            "zoom_in" => (s, c.X, c.Y, s * z, c.X, c.Y),
            "zoom_out" => (s * z, c.X, c.Y, s, c.X, c.Y),
            "pan_left" => (s * z, c.X + h, c.Y, s * z, c.X - h, c.Y),
            "pan_right" => (s * z, c.X - h, c.Y, s * z, c.X + h, c.Y),
            "pan_up" => (s * z, c.X, c.Y - h, s * z, c.X, c.Y + h),
            "pan_down" => (s * z, c.X, c.Y + h, s * z, c.X, c.Y - h),
            _ => (s, c.X, c.Y, s, c.X, c.Y),
        };
    }

    public static void SaveXml(XDocument x, string path)
    {
        var tmp = path + ".aitmp";
        x.Save(tmp);
        File.Move(tmp, path, true);
    }
}

/// <summary>Final Cut Pro 7 XML (xmeml v5) – imported by Premiere Pro, DaVinci Resolve, VEGAS Pro and others.</summary>
public sealed class XmemlWriter : ITimelineFormat
{
    public string Extension => ".xml";
    public TimelineDoc Load(string path) => throw new EditException("FCP7 XML is export-only");

    int ids;
    readonly Dictionary<string, string> fileIds = new(StringComparer.OrdinalIgnoreCase);
    static string F(double v) => v.ToString("0.####", ExportUtil.C);

    XElement Rate(TimelineDoc d) => new("rate", new XElement("timebase", d.IntFps), new XElement("ntsc", "FALSE"));

    public void Save(TimelineDoc d, string path)
    {
        ids = 0;
        fileIds.Clear();
        long total = d.Frames(d.Duration);
        var video = new XElement("video", new XElement("format", new XElement("samplecharacteristics", Rate(d),
            new XElement("width", d.Width), new XElement("height", d.Height), new XElement("anamorphic", "FALSE"),
            new XElement("pixelaspectratio", "square"), new XElement("fielddominance", "none"))));
        var audio = new XElement("audio", new XElement("numOutputChannels", 2),
            new XElement("format", new XElement("samplecharacteristics", new XElement("depth", 16), new XElement("samplerate", 48000))));
        // FCP7 lists video tracks bottom-up: main first
        foreach (var t in d.Tracks.Where(t => t.Type != "audio"))
            video.Add(VideoTrack(d, t));
        foreach (var t in d.Tracks.Where(t => t.Type == "video" && !t.Muted))
            if (t.Clips.Any(c => c.Kind == "video" && c.HasAudio)) audio.Add(AudioTrack(d, t, linked: true));
        foreach (var t in d.Tracks.Where(t => t.Type == "audio"))
            audio.Add(AudioTrack(d, t, linked: false));
        var seq = new XElement("sequence", new XAttribute("id", "sequence-1"),
            new XElement("uuid", Guid.NewGuid().ToString().ToUpperInvariant()), new XElement("name", d.Name),
            new XElement("duration", total), Rate(d),
            new XElement("timecode", Rate(d), new XElement("string", "00:00:00:00"), new XElement("frame", 0), new XElement("displayformat", "NDF")),
            new XElement("media", video, audio));
        var x = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XDocumentType("xmeml", null, null, null),
            new XElement("xmeml", new XAttribute("version", "5"), seq));
        ExportUtil.SaveXml(x, path);
    }

    XElement FileEl(TimelineDoc d, TClip c)
    {
        if (fileIds.TryGetValue(c.Path, out var fid)) return new XElement("file", new XAttribute("id", fid));
        fid = $"file-{fileIds.Count + 1}";
        fileIds[c.Path] = fid;
        var media = new XElement("media");
        if (c.Kind != "audio")
            media.Add(new XElement("video", new XElement("samplecharacteristics", Rate(d),
                new XElement("width", c.MediaWidth > 0 ? c.MediaWidth : d.Width), new XElement("height", c.MediaHeight > 0 ? c.MediaHeight : d.Height))));
        if (c.HasAudio || c.Kind == "audio")
            media.Add(new XElement("audio", new XElement("samplecharacteristics", new XElement("depth", 16), new XElement("samplerate", 48000)),
                new XElement("channelcount", 2)));
        long dur = c.Kind == "photo" ? d.Frames(3600L * T.US) : d.Frames(c.SourceDur > 0 ? c.SourceDur : c.In + c.Dur);
        return new XElement("file", new XAttribute("id", fid), new XElement("name", c.FileName),
            new XElement("pathurl", ExportUtil.FileUrl(c.Path, true)), Rate(d), new XElement("duration", dur), media);
    }

    static XElement Param(string id, string name, object value, IEnumerable<(long when, object v)>? keys = null)
    {
        var p = new XElement("parameter", new XAttribute("authoringApp", "PremierePro"), new XElement("parameterid", id), new XElement("name", name));
        if (keys != null)
            foreach (var (when, v) in keys)
                p.Add(new XElement("keyframe", new XElement("when", when), v is XElement[] arr ? new XElement("value", arr) : new XElement("value", v)));
        p.Add(value is XElement[] a ? new XElement("value", a) : new XElement("value", value));
        return p;
    }

    static XElement Effect(string name, string id, string cat, string type, string media, params XElement[] parms) =>
        new("filter", new XElement("effect", new XElement("name", name), new XElement("effectid", id),
            new XElement("effectcategory", cat), new XElement("effecttype", type), new XElement("mediatype", media), parms));

    XElement VideoTrack(TimelineDoc d, TTrack t)
    {
        var tr = new XElement("track");
        var clips = t.Clips.OrderBy(c => c.Start).ToList();
        for (int i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            long start = d.Frames(c.Start), end = d.Frames(c.End), len = end - start;
            if (c.Kind == "text")
            {
                tr.Add(new XElement("generatoritem", new XAttribute("id", $"generator-{++ids}"),
                    new XElement("name", "Text"), new XElement("duration", len), Rate(d), new XElement("start", start), new XElement("end", end),
                    new XElement("in", 0), new XElement("out", len), new XElement("enabled", "TRUE"),
                    new XElement("effect", new XElement("name", "Text"), new XElement("effectid", "Text"), new XElement("effectcategory", "Text"),
                        new XElement("effecttype", "generator"), new XElement("mediatype", "video"),
                        Param("str", "Text", c.Text ?? ""), Param("fontname", "Font", c.Font ?? "Arial"),
                        Param("fontsize", "Size", F(c.FontSize * 8 * d.Height / 1080.0)), Param("fontstyle", "Style", c.Bold ? 2 : 1),
                        Param("fontalign", "Alignment", c.Align == "left" ? 1 : c.Align == "right" ? 3 : 2),
                        Param("fontcolor", "Font Color", new[] { new XElement("alpha", 255), new XElement("red", Hex(c.Color, 0)),
                            new XElement("green", Hex(c.Color, 1)), new XElement("blue", Hex(c.Color, 2)) }),
                        Param("origin", "Origin", new[] { new XElement("horiz", F(c.X / 2)), new XElement("vert", F(-c.Y / 2)) }))));
                continue;
            }
            long inF = c.Kind == "photo" ? 0 : d.Frames(c.In);
            long srcLen = (long)Math.Round(len * c.Speed);
            var ci = new XElement("clipitem", new XAttribute("id", $"clipitem-{++ids}"),
                new XElement("masterclipid", $"masterclip-{c.Path.GetHashCode() & 0x7fffffff}"),
                new XElement("name", c.FileName), new XElement("enabled", t.Hidden ? "FALSE" : "TRUE"),
                new XElement("duration", c.Kind == "photo" ? d.Frames(3600L * T.US) : d.Frames(c.SourceDur > 0 ? c.SourceDur : c.In + c.Dur)),
                Rate(d), new XElement("start", start), new XElement("end", end), new XElement("in", inF), new XElement("out", inF + srcLen),
                FileEl(d, c));
            double fit = ExportUtil.Fit(d, c);
            if (c.Kind == "photo") ci.Add(new XElement("stillframe", "TRUE"));
            if (Math.Abs(c.Speed - 1) > 1e-6)
                ci.Add(Effect("Time Remap", "timeremap", "motion", "motion", "video",
                    Param("speed", "speed", F(c.Speed * 100)), Param("reverse", "reverse", "FALSE")));
            if (c.Motion != null)
            {
                var (s0, x0, y0, s1, x1, y1) = ExportUtil.Motion(c);
                ci.Add(Effect("Basic Motion", "basic", "motion", "motion", "video",
                    Param("scale", "Scale", F(s0 * fit * 100), [(0, F(s0 * fit * 100)), (len, F(s1 * fit * 100))]),
                    Param("rotation", "Rotation", F(c.Rotation)),
                    Param("center", "Center", Center(x0, y0), [(0, Center(x0, y0)), (len, Center(x1, y1))])));
            }
            else if (c.Scale != 1 || c.X != 0 || c.Y != 0 || c.Rotation != 0 || Math.Abs(fit - 1) > 1e-3)
                ci.Add(Effect("Basic Motion", "basic", "motion", "motion", "video",
                    Param("scale", "Scale", F(c.Scale * fit * 100)), Param("rotation", "Rotation", F(c.Rotation)),
                    Param("center", "Center", Center(c.X, c.Y))));
            if (c.Opacity != 1 || c.VideoFadeIn > 0 || c.VideoFadeOut > 0)
            {
                var keys = new List<(long, object)>();
                double op = c.Opacity * 100;
                if (c.VideoFadeIn > 0) { keys.Add((0, 0)); keys.Add((d.Frames(c.VideoFadeIn), F(op))); }
                if (c.VideoFadeOut > 0) { keys.Add((len - d.Frames(c.VideoFadeOut), F(op))); keys.Add((len, 0)); }
                ci.Add(Effect("Opacity", "opacity", "motion", "motion", "video", Param("opacity", "opacity", F(op), keys.Count > 0 ? keys : null)));
            }
            tr.Add(ci);
            if (c.Transition != null && i + 1 < clips.Count && clips[i + 1].Start == c.End)
            {
                long td = d.Frames(c.TransitionDur);
                var (name, id, cat) = c.Transition switch
                {
                    "Dip to Black" => ("Dip to Black", "Dip to Black", "Dissolve"),
                    "Dip to White" => ("Dip to White", "Dip to White", "Dissolve"),
                    "Wipe" => ("Wipe", "Wipe", "Wipe"),
                    _ => ("Cross Dissolve", "Cross Dissolve", "Dissolve"),
                };
                tr.Add(new XElement("transitionitem", new XElement("start", end - td / 2), new XElement("end", end + (td - td / 2)),
                    new XElement("alignment", "center"), Rate(d),
                    new XElement("effect", new XElement("name", name), new XElement("effectid", id), new XElement("effectcategory", cat),
                        new XElement("effecttype", "transition"), new XElement("mediatype", "video"))));
            }
        }
        tr.Add(new XElement("enabled", t.Hidden ? "FALSE" : "TRUE"), new XElement("locked", "FALSE"));
        return tr;
    }

    static XElement[] Center(double x, double y) => [new XElement("horiz", F(x / 2)), new XElement("vert", F(-y / 2))];

    XElement AudioTrack(TimelineDoc d, TTrack t, bool linked)
    {
        var tr = new XElement("track");
        foreach (var c in t.Clips.OrderBy(c => c.Start))
        {
            if (linked && !(c.Kind == "video" && c.HasAudio)) continue;
            long start = d.Frames(c.Start), end = d.Frames(c.End), len = end - start, inF = d.Frames(c.In);
            var ci = new XElement("clipitem", new XAttribute("id", $"clipitem-{++ids}"), new XElement("name", c.FileName),
                new XElement("enabled", "TRUE"), new XElement("duration", d.Frames(c.SourceDur > 0 ? c.SourceDur : c.In + c.Dur)), Rate(d),
                new XElement("start", start), new XElement("end", end), new XElement("in", inF), new XElement("out", inF + (long)Math.Round(len * c.Speed)),
                FileEl(d, c), new XElement("sourcetrack", new XElement("mediatype", "audio"), new XElement("trackindex", 1)));
            double lvl = c.Volume;
            var keys = new List<(long, object)>();
            if (c.AudioFadeIn > 0) { keys.Add((0, 0)); keys.Add((d.Frames(c.AudioFadeIn), F(lvl))); }
            if (c.AudioFadeOut > 0) { keys.Add((len - d.Frames(c.AudioFadeOut), F(lvl))); keys.Add((len, 0)); }
            if (lvl != 1 || keys.Count > 0)
                ci.Add(Effect("Audio Levels", "audiolevels", "audiolevels", "audiolevels", "audio",
                    Param("level", "Level", F(lvl), keys.Count > 0 ? keys : null)));
            tr.Add(ci);
        }
        tr.Add(new XElement("enabled", t.Muted ? "FALSE" : "TRUE"), new XElement("locked", "FALSE"));
        return tr;
    }

    static int Hex(string color, int i) => Convert.ToInt32(color.TrimStart('#').Substring(i * 2, 2), 16);
}
