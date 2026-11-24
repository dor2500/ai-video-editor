using System.Xml.Linq;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core.Formats;

/// <summary>FCPXML 1.10 – imported by Final Cut Pro and DaVinci Resolve.</summary>
public sealed class FcpxmlWriter : ITimelineFormat
{
    public string Extension => ".fcpxml";
    public TimelineDoc Load(string path) => throw new EditException("FCPXML is export-only");

    const string TitleUid = ".../Titles.localized/Bumper:Opener.localized/Basic Title.localized/Basic Title.moti";
    const string DissolveUid = "FxPlug:4731E73A-8DAC-4113-9A30-AE85B1761265";
    static string F(double v) => v.ToString("0.####", ExportUtil.C);

    TimelineDoc d = null!;
    string Tm(long us) => $"{d.Frames(us)}/{d.IntFps}s";
    string TmF(long frames) => $"{frames}/{d.IntFps}s";

    public void Save(TimelineDoc doc, string path)
    {
        d = doc;
        var res = new XElement("resources",
            new XElement("format", new XAttribute("id", "r0"), new XAttribute("name", $"FFVideoFormat{d.Height}p{d.IntFps}"),
                new XAttribute("frameDuration", $"1/{d.IntFps}s"), new XAttribute("width", d.Width), new XAttribute("height", d.Height)),
            new XElement("effect", new XAttribute("id", "rTitle"), new XAttribute("name", "Basic Title"), new XAttribute("uid", TitleUid)),
            new XElement("effect", new XAttribute("id", "rDiss"), new XAttribute("name", "Cross Dissolve"), new XAttribute("uid", DissolveUid)));
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Asset(TClip c)
        {
            if (assets.TryGetValue(c.Path, out var id)) return id;
            id = $"a{assets.Count + 1}";
            assets[c.Path] = id;
            bool still = c.Kind == "photo";
            res.Add(new XElement("asset", new XAttribute("id", id), new XAttribute("name", Path.GetFileNameWithoutExtension(c.Path)),
                new XAttribute("start", "0s"), new XAttribute("duration", still ? "0s" : Tm(c.SourceDur > 0 ? c.SourceDur : c.In + c.Dur)),
                new XAttribute("hasVideo", c.Kind == "audio" ? "0" : "1"), new XAttribute("hasAudio", c.HasAudio || c.Kind == "audio" ? "1" : "0"),
                new XAttribute("format", "r0"), new XAttribute("audioSources", "1"), new XAttribute("audioChannels", "2"),
                new XElement("media-rep", new XAttribute("kind", "original-media"), new XAttribute("src", ExportUtil.FileUrl(c.Path, false)))));
            return id;
        }

        // primary storyline = main video track (with gaps)
        var main = d.Tracks.FirstOrDefault(t => t.Type == "video") ?? new TTrack();
        var spine = new XElement("spine");
        var spineItems = new List<(XElement el, long offset, long dur, long start)>();
        long cur = 0;
        var mainClips = main.Clips.OrderBy(c => c.Start).ToList();
        long total = d.Frames(d.Duration);
        for (int i = 0; i < mainClips.Count; i++)
        {
            var c = mainClips[i];
            long s = d.Frames(c.Start), len = d.Frames(c.End) - s;
            if (s > cur) AddGap(cur, s - cur);
            if (s < cur) { s = cur; }
            var el = ClipEl(c, Asset(c), s, len, null);
            spine.Add(el);
            spineItems.Add((el, s, len, c.Kind == "photo" ? 0 : d.Frames(c.In)));
            cur = s + len;
            if (c.Transition != null && i + 1 < mainClips.Count && mainClips[i + 1].Start == c.End)
            {
                long td = d.Frames(c.TransitionDur);
                spine.Add(new XElement("transition", new XAttribute("name", c.Transition), new XAttribute("offset", TmF(cur - td / 2)),
                    new XAttribute("duration", TmF(td)), new XElement("filter-video", new XAttribute("ref", "rDiss"), new XAttribute("name", "Cross Dissolve"))));
            }
        }
        if (total > cur) AddGap(cur, total - cur);
        void AddGap(long off, long len)
        {
            var g = new XElement("gap", new XAttribute("name", "Gap"), new XAttribute("offset", TmF(off)),
                new XAttribute("duration", TmF(len)), new XAttribute("start", TmF(off)));
            spine.Add(g);
            spineItems.Add((g, off, len, off));
        }

        // everything else is connected to whatever spine item covers its start
        int lane = 0;
        foreach (var t in d.Tracks.Where(t => t != main && t.Type != "audio")) { lane++; Connect(t, lane); }
        lane = 0;
        foreach (var t in d.Tracks.Where(t => t.Type == "audio")) { lane--; Connect(t, lane); }
        void Connect(TTrack t, int ln)
        {
            foreach (var c in t.Clips)
            {
                long s = d.Frames(c.Start), len = d.Frames(c.End) - s;
                var host = spineItems.LastOrDefault(x => x.offset <= s && s < x.offset + x.dur);
                if (host.el == null) continue;
                long local = host.start + (s - host.offset);
                XElement el = c.Kind == "text" ? TitleEl(c, local, len, ln) : ClipEl(c, Asset(c), local, len, ln);
                if (t.Muted && c.Kind != "text") el.Add(new XElement("adjust-volume", new XAttribute("amount", "-96dB")));
                host.el.Add(el);
            }
        }

        var seq = new XElement("sequence", new XAttribute("format", "r0"), new XAttribute("duration", TmF(total)),
            new XAttribute("tcStart", "0s"), new XAttribute("tcFormat", "NDF"), new XAttribute("audioLayout", "stereo"),
            new XAttribute("audioRate", "48k"), spine);
        var x = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XDocumentType("fcpxml", null, null, null),
            new XElement("fcpxml", new XAttribute("version", "1.10"), res,
                new XElement("library", new XElement("event", new XAttribute("name", "AI Video Editor"),
                    new XElement("project", new XAttribute("name", d.Name), seq)))));
        ExportUtil.SaveXml(x, path);
    }

    XElement ClipEl(TClip c, string asset, long offset, long len, int? lane)
    {
        long inF = c.Kind == "photo" ? 0 : d.Frames(c.In);
        var el = new XElement("asset-clip", new XAttribute("ref", asset), new XAttribute("offset", TmF(offset)),
            new XAttribute("name", c.FileName), new XAttribute("start", TmF(inF)), new XAttribute("duration", TmF(len)),
            new XAttribute("tcFormat", "NDF"));
        if (lane is { } l) el.Add(new XAttribute("lane", l));
        if (c.Kind == "audio") el.Add(new XAttribute("audioRole", "music"));
        if (c.Kind != "audio")
        {
            double ar = (double)d.Width / d.Height;
            string Pos(double x, double y) => $"{F(x * ar * 50)} {F(y * 50)}";
            if (c.Motion != null)
            {
                var (s0, x0, y0, s1, x1, y1) = ExportUtil.Motion(c);
                el.Add(new XElement("adjust-transform", new XAttribute("rotation", F(-c.Rotation)),
                    KeyParam("position", inF, len, Pos(x0, y0), Pos(x1, y1)),
                    KeyParam("scale", inF, len, $"{F(s0)} {F(s0)}", $"{F(s1)} {F(s1)}")));
            }
            else if (c.Scale != 1 || c.X != 0 || c.Y != 0 || c.Rotation != 0 || c.FlipH || c.FlipV)
                el.Add(new XElement("adjust-transform", new XAttribute("position", Pos(c.X, c.Y)),
                    new XAttribute("scale", $"{F(c.Scale * (c.FlipH ? -1 : 1))} {F(c.Scale * (c.FlipV ? -1 : 1))}"),
                    new XAttribute("rotation", F(-c.Rotation))));
            if (c.Opacity != 1 || c.VideoFadeIn > 0 || c.VideoFadeOut > 0)
            {
                var blend = new XElement("adjust-blend", new XAttribute("amount", F(c.Opacity)));
                if (c.VideoFadeIn > 0 || c.VideoFadeOut > 0)
                {
                    var ka = new XElement("keyframeAnimation");
                    if (c.VideoFadeIn > 0) { ka.Add(Kf(inF, "0")); ka.Add(Kf(inF + d.Frames(c.VideoFadeIn), F(c.Opacity))); }
                    if (c.VideoFadeOut > 0) { ka.Add(Kf(inF + len - d.Frames(c.VideoFadeOut), F(c.Opacity))); ka.Add(Kf(inF + len, "0")); }
                    blend.Add(new XElement("param", new XAttribute("name", "amount"), ka));
                }
                el.Add(blend);
            }
        }
        if (c.Kind != "photo")
        {
            var vol = new XElement("adjust-volume", new XAttribute("amount", $"{F(c.Volume <= 0 ? -96 : 20 * Math.Log10(c.Volume))}dB"));
            if (c.AudioFadeIn > 0 || c.AudioFadeOut > 0)
            {
                var p = new XElement("param", new XAttribute("name", "amount"));
                if (c.AudioFadeIn > 0) p.Add(new XElement("fadeIn", new XAttribute("type", "easeIn"), new XAttribute("duration", Tm(c.AudioFadeIn))));
                if (c.AudioFadeOut > 0) p.Add(new XElement("fadeOut", new XAttribute("type", "easeIn"), new XAttribute("duration", Tm(c.AudioFadeOut))));
                vol.Add(p);
            }
            if (c.Volume != 1 || c.AudioFadeIn > 0 || c.AudioFadeOut > 0) el.Add(vol);
        }
        if (Math.Abs(c.Speed - 1) > 1e-6)
            el.Add(new XElement("timeMap", new XElement("timept", new XAttribute("time", "0s"), new XAttribute("value", TmF(inF)), new XAttribute("interp", "linear")),
                new XElement("timept", new XAttribute("time", TmF(len)), new XAttribute("value", TmF(inF + (long)(len * c.Speed))), new XAttribute("interp", "linear"))));
        return el;
    }

    XElement Kf(long frame, string value) => new("keyframe", new XAttribute("time", TmF(frame)), new XAttribute("value", value));

    XElement KeyParam(string name, long inF, long len, string v0, string v1) =>
        new("param", new XAttribute("name", name), new XElement("keyframeAnimation", Kf(inF, v0), Kf(inF + len, v1)));

    XElement TitleEl(TClip c, long offset, long len, int lane)
    {
        var id = "ts" + c.Id;
        var rgb = Colors.Rgb(c.Color);
        var style = new XElement("text-style", new XAttribute("font", c.Font ?? "Helvetica"),
            new XAttribute("fontSize", F(c.FontSize * 8 * d.Height / 1080.0)), new XAttribute("fontColor", $"{F(rgb[0])} {F(rgb[1])} {F(rgb[2])} 1"),
            new XAttribute("bold", c.Bold ? "1" : "0"), new XAttribute("italic", c.Italic ? "1" : "0"), new XAttribute("alignment", c.Align));
        if (c.StrokeColor != null)
        {
            var s = Colors.Rgb(c.StrokeColor);
            style.Add(new XAttribute("strokeColor", $"{F(s[0])} {F(s[1])} {F(s[2])} 1"), new XAttribute("strokeWidth", "3"));
        }
        double ar = (double)d.Width / d.Height;
        return new XElement("title", new XAttribute("ref", "rTitle"), new XAttribute("lane", lane), new XAttribute("offset", TmF(offset)),
            new XAttribute("name", c.Text ?? "Title"), new XAttribute("start", "0s"), new XAttribute("duration", TmF(len)),
            new XElement("param", new XAttribute("name", "Position"), new XAttribute("key", "9999/999166631/999166633/1/100/101"),
                new XAttribute("value", $"{F(c.X * ar * 50 * d.Height / 100.0)} {F(c.Y * 50 * d.Height / 100.0)}")),
            new XElement("text", new XElement("text-style", new XAttribute("ref", id), c.Text ?? "")),
            new XElement("text-style-def", new XAttribute("id", id), style));
    }
}
