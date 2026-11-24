using System.Globalization;
using System.Xml.Linq;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core.Formats;

/// <summary>
/// Shotcut project (.mlt, MLT XML). Load reads tracks/clips/basic filters; Save writes a complete Shotcut-style
/// project. Filters the model doesn't understand are kept as raw XML on the clip and written back.
/// </summary>
public sealed class MltFormat : ITimelineFormat
{
    public string Extension => ".mlt";
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ load
    public TimelineDoc Load(string path)
    {
        var x = XDocument.Load(path);
        var root = x.Root ?? throw new EditException("Empty MLT file");
        var doc = new TimelineDoc { Name = Path.GetFileNameWithoutExtension(path) };
        if (root.Element("profile") is { } prof)
        {
            doc.Width = (int)Num(prof.Attribute("width")?.Value, 1920);
            doc.Height = (int)Num(prof.Attribute("height")?.Value, 1080);
            var fn = Num(prof.Attribute("frame_rate_num")?.Value, 30);
            var fd = Num(prof.Attribute("frame_rate_den")?.Value, 1);
            doc.Fps = fd > 0 ? fn / fd : 30;
        }
        var byId = root.Elements().Where(e => e.Attribute("id") != null)
            .GroupBy(e => e.Attribute("id")!.Value).ToDictionary(g => g.Key, g => g.Last());
        // the main tractor is the last top-level tractor
        var main = root.Elements("tractor").LastOrDefault() ?? throw new EditException("No tractor in MLT file");
        foreach (var tr in main.Elements("track"))
        {
            var pid = tr.Attribute("producer")?.Value ?? "";
            if (pid == "background" || !byId.TryGetValue(pid, out var pl) || pl.Name != "playlist") continue;
            var hide = tr.Attribute("hide")?.Value ?? "";
            bool isAudio = Prop(pl, "shotcut:audio") == "1" || hide == "video";
            var track = new TTrack
            {
                Type = isAudio ? "audio" : "video", Name = Prop(pl, "shotcut:name") ?? "",
                Muted = hide is "audio" or "both", Hidden = hide is "video" && !isAudio || hide == "both",
            };
            long cursor = 0, carry = 0;
            TClip? prev = null;
            foreach (var el in pl.Elements())
            {
                if (el.Name == "blank") { cursor += doc.FromFrames(Frames(el.Attribute("length")?.Value, doc.Fps)); prev = null; carry = 0; continue; }
                if (el.Name != "entry") continue;
                var eid = el.Attribute("producer")?.Value ?? "";
                long fin = Frames(el.Attribute("in")?.Value, doc.Fps), fout = Frames(el.Attribute("out")?.Value, doc.Fps);
                long len = doc.FromFrames(fout - fin + 1);
                if (!byId.TryGetValue(eid, out var prod)) { cursor += len; continue; }
                if (prod.Name == "tractor")
                {
                    // a Shotcut transition = the tail of the previous clip blended with the head of the next one
                    if (prev != null)
                    {
                        prev.Dur += len;
                        prev.Transition = (Prop(prod, "shotcut:transition") ?? "").Contains("luma") &&
                                          prod.Descendants("property").Any(p => p.Attribute("name")?.Value == "resource" && p.Value.Contains("luma"))
                            ? "Wipe" : "Cross Dissolve";
                        prev.TransitionDur = len;
                        carry = len;
                    }
                    cursor += len;
                    continue;
                }
                var clip = ClipFrom(prod, el, doc, fin);
                clip.Start = cursor;
                clip.Dur = len;
                if (carry > 0 && clip.Kind is "video" or "audio") clip.In = Math.Max(0, clip.In - carry);
                carry = 0;
                track.Clips.Add(clip);
                prev = clip;
                cursor += len;
            }
            if (!isAudio && track.Clips.Count > 0 && track.Clips.All(c => c.Kind == "text")) track.Type = "text";
            doc.Tracks.Add(track);
        }
        // keep the main track first
        var firstVideo = doc.Tracks.FirstOrDefault(t => t.Type == "video");
        if (firstVideo != null) { doc.Tracks.Remove(firstVideo); doc.Tracks.Insert(0, firstVideo); }
        return doc;
    }

    TClip ClipFrom(XElement prod, XElement entry, TimelineDoc doc, long fin)
    {
        var svc = Prop(prod, "mlt_service") ?? "";
        var res = Prop(prod, "resource") ?? "";
        var c = new TClip();
        var filters = prod.Elements("filter").Concat(entry.Elements("filter")).ToList();
        var keep = new List<string>();
        var qtext = filters.FirstOrDefault(f => Prop(f, "mlt_service") is "qtext" or "dynamictext");
        if (svc == "color" && qtext != null)
        {
            c.Kind = "text";
            c.Text = Prop(qtext, "argument") ?? "";
            c.Color = ArgbToHex(Prop(qtext, "fgcolour") ?? "#ffffffff");
            c.FontSize = Num(Prop(qtext, "size"), 64) / 8.0 / (doc.Height / 1080.0);
            c.Bold = (Prop(qtext, "weight") ?? "") is "700" or "bold";
            if (Prop(qtext, "olcolour") is { } ol && Num(Prop(qtext, "outline"), 0) > 0) c.StrokeColor = ArgbToHex(ol);
            if (Prop(qtext, "family") is { Length: > 0 } fam) c.Font = fam;
            var geo = (Prop(qtext, "geometry") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => Num(s, 0)).ToArray();
            if (geo.Length >= 4)
            {
                c.X = (geo[0] + geo[2] / 2) / (doc.Width / 2.0) - 1;
                c.Y = 1 - (geo[1] + geo[3] / 2) / (doc.Height / 2.0);
            }
        }
        else
        {
            var file = res;
            double speed = 1;
            if (svc == "timewarp" && res.Contains(':'))
            {
                var i = res.IndexOf(':');
                speed = Num(res[..i], 1);
                file = res[(i + 1)..];
            }
            c.Path = file.Replace('/', '\\');
            c.Speed = speed;
            var kind = Media.KindOf(file);
            c.Kind = kind == MediaKind.Photo || svc is "qimage" or "pixbuf" ? "photo" : kind == MediaKind.Audio ? "audio" : "video";
            c.In = c.Kind == "photo" ? 0 : doc.FromFrames(fin);
            c.SourceDur = c.Kind == "photo" ? 0 : doc.FromFrames(Frames(Prop(prod, "length"), doc.Fps));
            c.HasAudio = c.Kind != "photo";
        }
        foreach (var f in filters)
        {
            var fs = Prop(f, "mlt_service") ?? "";
            var sf = Prop(f, "shotcut:filter") ?? "";
            if (f == qtext) continue;
            if (fs == "volume" && sf == "fadeInVolume") c.AudioFadeIn = FadeLen(f, doc);
            else if (fs == "volume" && sf == "fadeOutVolume") c.AudioFadeOut = FadeLen(f, doc);
            else if (fs == "volume" && Prop(f, "level") is { } lv && !lv.Contains('='))
                c.Volume = Math.Pow(10, Num(lv, 0) / 20.0);
            else if (fs == "brightness" && sf == "fadeInBrightness") c.VideoFadeIn = FadeLen(f, doc);
            else if (fs == "brightness" && sf == "fadeOutBrightness") c.VideoFadeOut = FadeLen(f, doc);
            else if (fs == "affine" && Prop(f, "transition.rect") is { } rect && !rect.Contains('='))
            {
                var r = rect.Replace("%", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => Num(s, 0)).ToArray();
                if (r.Length >= 4 && r[2] > 0)
                {
                    c.Scale = r[2] / doc.Width;
                    c.X = (r[0] + r[2] / 2) / (doc.Width / 2.0) - 1;
                    c.Y = 1 - (r[1] + r[3] / 2) / (doc.Height / 2.0);
                    if (r.Length >= 5) c.Opacity = r[4];
                }
                c.Rotation = Num(Prop(f, "transition.fix_rotate_x"), 0);
            }
            else keep.Add(f.ToString(SaveOptions.DisableFormatting));
        }
        if (keep.Count > 0) c.Extra["mltFilters"] = string.Join("\n", keep);
        return c;
    }

    static long FadeLen(XElement f, TimelineDoc doc)
    {
        var lv = Prop(f, "level") ?? Prop(f, "alpha") ?? "";
        var parts = lv.Split(';');
        if (parts.Length < 2) return doc.FromFrames(doc.IntFps);
        long a = Frames(parts[0].Split('=')[0], doc.Fps), b = Frames(parts[^1].Split('=')[0], doc.Fps);
        return doc.FromFrames(Math.Abs(b - a));
    }

    static string? Prop(XElement e, string name) =>
        e.Elements("property").FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Value;

    static double Num(string? s, double def) =>
        double.TryParse(s, NumberStyles.Float, C, out var v) ? v : def;

    /// <summary>MLT time: frames, "HH:MM:SS.mmm" or "HH:MM:SS:FF".</summary>
    static long Frames(string? s, double fps)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        if (long.TryParse(s, out var f)) return f;
        var p = s.Split(':');
        if (p.Length == 3)
        {
            double sec = Num(p[0], 0) * 3600 + Num(p[1], 0) * 60 + Num(p[2], 0);
            return (long)Math.Round(sec * fps);
        }
        if (p.Length == 4) return (long)Math.Round((Num(p[0], 0) * 3600 + Num(p[1], 0) * 60 + Num(p[2], 0)) * fps + Num(p[3], 0));
        return 0;
    }

    static string ArgbToHex(string c)
    {
        c = c.TrimStart('#');
        if (c.Length == 8) c = c[2..]; // #aarrggbb
        return "#" + (c.Length == 6 ? c.ToUpperInvariant() : "FFFFFF");
    }

    // ------------------------------------------------------------------ save
    int nextId;
    string NewId(string prefix) => $"{prefix}{nextId++}";

    static XElement P(string name, object value) =>
        new("property", new XAttribute("name", name), Convert.ToString(value, C));

    public void Save(TimelineDoc doc, string path)
    {
        nextId = 0;
        int fps = doc.IntFps;
        long total = Math.Max(doc.Frames(doc.Duration), 1);
        var mlt = new XElement("mlt", new XAttribute("LC_NUMERIC", "C"), new XAttribute("version", "7.24.0"),
            new XAttribute("title", "AI Video Editor"), new XAttribute("producer", "main_bin"));
        int g = Gcd(doc.Width, doc.Height);
        mlt.Add(new XElement("profile",
            new XAttribute("description", $"{doc.Width}x{doc.Height} {fps}fps"), new XAttribute("width", doc.Width),
            new XAttribute("height", doc.Height), new XAttribute("progressive", 1), new XAttribute("sample_aspect_num", 1),
            new XAttribute("sample_aspect_den", 1), new XAttribute("display_aspect_num", doc.Width / g),
            new XAttribute("display_aspect_den", doc.Height / g), new XAttribute("frame_rate_num", fps),
            new XAttribute("frame_rate_den", 1), new XAttribute("colorspace", 709)));
        mlt.Add(new XElement("playlist", new XAttribute("id", "main_bin"), P("xml_retain", 1)));
        mlt.Add(new XElement("producer", new XAttribute("id", "black"), new XAttribute("in", 0), new XAttribute("out", total - 1),
            P("length", total), P("eof", "pause"), P("resource", "0"), P("aspect_ratio", 1), P("mlt_service", "color"),
            P("mlt_image_format", "rgba"), P("set.test_audio", 0)));
        mlt.Add(new XElement("playlist", new XAttribute("id", "background"),
            new XElement("entry", new XAttribute("producer", "black"), new XAttribute("in", 0), new XAttribute("out", total - 1))));

        var tractor = new XElement("tractor", new XAttribute("id", "tractor_main"), new XAttribute("title", "AI Video Editor"),
            new XAttribute("in", 0), new XAttribute("out", total - 1), P("shotcut", 1), P("shotcut:projectAudioChannels", 2),
            P("shotcut:projectFolder", 0));
        tractor.Add(new XElement("track", new XAttribute("producer", "background")));

        // Shotcut lists video tracks top-down as V1..Vn after background; MLT index 1 is the bottom video track.
        var videoTracks = doc.Tracks.Where(t => t.Type is "video" or "text").ToList();
        var audioTracks = doc.Tracks.Where(t => t.Type == "audio").ToList();
        int vi = 0, ai = 0, mltIndex = 0;
        var composites = new List<int>();
        var audioIdx = new List<int>();
        foreach (var t in videoTracks.Concat(audioTracks))
        {
            bool isAudio = t.Type == "audio";
            var plId = NewId("playlist");
            var pl = new XElement("playlist", new XAttribute("id", plId));
            pl.Add(isAudio ? P("shotcut:audio", 1) : P("shotcut:video", 1));
            pl.Add(P("shotcut:name", t.Name is { Length: > 0 } n ? n : isAudio ? $"A{++ai}" : $"V{++vi}"));
            var producers = WritePlaylist(doc, t, pl);
            foreach (var p in producers) mlt.Add(p);
            mlt.Add(pl);
            mltIndex++;
            var trackEl = new XElement("track", new XAttribute("producer", plId));
            string? hide = isAudio ? (t.Muted ? "both" : "video") : t.Muted && t.Hidden ? "both" : t.Muted ? "audio" : t.Hidden ? "video" : null;
            if (hide != null) trackEl.Add(new XAttribute("hide", hide));
            tractor.Add(trackEl);
            if (!isAudio) composites.Add(mltIndex);
            audioIdx.Add(mltIndex);
        }
        foreach (var i in audioIdx)
            tractor.Add(new XElement("transition", new XAttribute("id", NewId("transition")),
                P("a_track", 0), P("b_track", i), P("mlt_service", "mix"), P("always_active", 1), P("sum", 1)));
        foreach (var i in composites)
            tractor.Add(new XElement("transition", new XAttribute("id", NewId("transition")),
                P("a_track", 0), P("b_track", i), P("version", "0.1"), P("mlt_service", "frei0r.cairoblend"), P("threads", 0),
                P("disable", i == composites.FirstOrDefault() ? 1 : 0)));
        mlt.Add(tractor);
        var xd = new XDocument(new XDeclaration("1.0", "utf-8", "no"), mlt);
        var tmp = path + ".aitmp";
        xd.Save(tmp);
        File.Move(tmp, path, true);
    }

    List<XElement> WritePlaylist(TimelineDoc doc, TTrack t, XElement pl)
    {
        var producers = new List<XElement>();
        long cursorF = 0;
        var clips = t.Clips.OrderBy(c => c.Start).ToList();
        long carryIn = 0; // frames consumed from the head of this clip by a previous transition
        for (int i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            long startF = doc.Frames(c.Start), lenF = Math.Max(doc.Frames(c.Dur), 1);
            if (startF != cursorF) carryIn = 0;
            if (startF > cursorF)
            {
                pl.Add(new XElement("blank", new XAttribute("length", startF - cursorF)));
                cursorF = startF;
            }
            // after a transition this clip starts carryIn frames later in its media; keep its full length if the media allows
            long avail = c.Kind == "photo" || c.SourceDur <= 0 ? long.MaxValue : doc.Frames(c.SourceDur) - doc.Frames(c.In);
            long visible = carryIn + lenF <= avail ? lenF : lenF - carryIn;
            var next = i + 1 < clips.Count && doc.Frames(clips[i + 1].Start) == startF + lenF ? clips[i + 1] : null;
            long transF = c.Transition != null && next != null && t.Type != "audio"
                ? Math.Min(doc.Frames(c.TransitionDur), Math.Min(visible, doc.Frames(next.Dur)) - 1) : 0;
            if (transF < 2) transF = 0;
            long inF = doc.Frames(c.In) + carryIn;
            long bodyF = visible - transF;
            var (prod, pid) = Producer(doc, c, t);
            producers.Add(prod);
            if (bodyF > 0)
            {
                pl.Add(new XElement("entry", new XAttribute("producer", pid), new XAttribute("in", inF), new XAttribute("out", inF + bodyF - 1)));
                cursorF += bodyF;
            }
            carryIn = 0;
            if (transF > 0 && next != null)
            {
                // A's last transF frames dissolve into B's first transF frames; B then continues after them.
                var (prodB, pidB) = Producer(doc, next, t);
                producers.Add(prodB);
                long aIn = inF + bodyF, bIn = doc.Frames(next.In);
                var tr = new XElement("tractor", new XAttribute("id", NewId("tractor")), new XAttribute("in", 0), new XAttribute("out", transF - 1),
                    P("shotcut:transition", "lumaMix"),
                    new XElement("track", new XAttribute("producer", pid), new XAttribute("in", aIn), new XAttribute("out", aIn + transF - 1)),
                    new XElement("track", new XAttribute("producer", pidB), new XAttribute("in", bIn), new XAttribute("out", bIn + transF - 1)));
                var luma = new XElement("transition", new XAttribute("id", NewId("transition")), new XAttribute("out", transF - 1),
                    P("a_track", 0), P("b_track", 1), P("factory", "loader"), P("mlt_service", "luma"), P("alpha_over", 1),
                    P("fix_background_alpha", 1));
                if (c.Transition == "Wipe") luma.Add(P("resource", "%luma01.pgm"));
                tr.Add(luma);
                tr.Add(new XElement("transition", new XAttribute("id", NewId("transition")), new XAttribute("out", transF - 1),
                    P("a_track", 0), P("b_track", 1), P("start", -1), P("accepts_blanks", 1), P("mlt_service", "mix")));
                producers.Add(tr);
                pl.Add(new XElement("entry", new XAttribute("producer", tr.Attribute("id")!.Value), new XAttribute("in", 0), new XAttribute("out", transF - 1)));
                cursorF += transF;
                carryIn = transF;
            }
        }
        return producers;
    }

    (XElement el, string id) Producer(TimelineDoc doc, TClip c, TTrack t)
    {
        int W = doc.Width, H = doc.Height;
        long lenF = Math.Max(doc.Frames(c.Dur), 1);
        XElement el;
        string id;
        if (c.Kind == "text")
        {
            id = NewId("producer");
            el = new XElement("producer", new XAttribute("id", id), new XAttribute("in", 0), new XAttribute("out", lenF - 1),
                P("length", lenF + 1), P("eof", "pause"), P("resource", "#00000000"), P("aspect_ratio", 1), P("mlt_service", "color"),
                P("mlt_image_format", "rgba"), P("shotcut:caption", "Text"));
            double px = c.FontSize * 8 * (H / 1080.0);
            double cx = (c.X + 1) / 2 * W, cy = (1 - c.Y) / 2 * H, boxH = px * 2.2;
            var txt = new XElement("filter", new XAttribute("id", NewId("filter")),
                P("argument", c.Text ?? ""), P("geometry", $"0 {Math.Round(cy - boxH / 2)} {W} {Math.Round(boxH)} 1"),
                P("family", c.Font ?? "Arial"), P("size", Math.Round(px)), P("weight", c.Bold ? 700 : 400),
                P("style", c.Italic ? "italic" : "normal"), P("fgcolour", "#ff" + c.Color.TrimStart('#').ToLowerInvariant()),
                P("bgcolour", c.BackgroundColor != null ? "#99" + c.BackgroundColor.TrimStart('#').ToLowerInvariant() : "#00000000"),
                P("olcolour", "#ff" + (c.StrokeColor ?? "#000000").TrimStart('#').ToLowerInvariant()),
                P("outline", c.StrokeColor != null ? 3 : 0), P("pad", 0),
                P("halign", c.Align == "left" ? "left" : c.Align == "right" ? "right" : "center"), P("valign", "middle"),
                P("mlt_service", "qtext"), P("shotcut:filter", "richText"));
            // horizontal placement other than centered: shift the box
            if (Math.Abs(cx - W / 2.0) > 1)
                txt.Elements("property").First(p => p.Attribute("name")!.Value == "geometry").Value =
                    $"{Math.Round(cx - W / 2.0)} {Math.Round(cy - boxH / 2)} {W} {Math.Round(boxH)} 1";
            el.Add(txt);
        }
        else
        {
            var file = c.Path.Replace('\\', '/');
            bool photo = c.Kind == "photo";
            if (photo)
            {
                id = NewId("producer");
                el = new XElement("producer", new XAttribute("id", id), new XAttribute("in", 0), new XAttribute("out", 15000 - 1),
                    P("length", 15000), P("eof", "pause"), P("resource", file), P("ttl", 1), P("aspect_ratio", 1),
                    P("meta.media.progressive", 1), P("seekable", 1), P("mlt_service", "qimage"),
                    P("shotcut:caption", c.FileName), P("xml", "was here"));
            }
            else if (Math.Abs(c.Speed - 1) > 1e-6)
            {
                id = NewId("producer");
                long srcF = c.SourceDur > 0 ? (long)(doc.Frames(c.SourceDur) / c.Speed) : lenF + doc.Frames(c.In) + 1;
                el = new XElement("producer", new XAttribute("id", id), new XAttribute("in", 0), new XAttribute("out", srcF - 1),
                    P("length", srcF), P("eof", "pause"), P("resource", $"{c.Speed.ToString(C)}:{file}"), P("warp_speed", c.Speed),
                    P("warp_resource", file), P("mlt_service", "timewarp"), P("shotcut:caption", c.FileName));
            }
            else
            {
                id = NewId("chain");
                long srcF = c.SourceDur > 0 ? doc.Frames(c.SourceDur) : lenF + doc.Frames(c.In) + 1;
                el = new XElement("chain", new XAttribute("id", id), new XAttribute("out", srcF - 1),
                    P("length", srcF), P("eof", "pause"), P("resource", file), P("mlt_service", "avformat-novalidate"),
                    P("seekable", 1), P("shotcut:caption", c.FileName));
            }
            if (t.Type == "audio") el.Add(P("video_index", -1));
            if (c.Kind != "photo" && Math.Abs(c.Volume - 1) > 1e-6)
                el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), P("window", 75), P("max_gain", "20dB"),
                    P("level", (c.Volume <= 0 ? -60 : 20 * Math.Log10(c.Volume)).ToString("0.##", C)), P("mlt_service", "volume"),
                    P("shotcut:filter", "audioGain")));
            if (c.AudioFadeIn > 0)
                el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), new XAttribute("in", doc.Frames(c.In)),
                    new XAttribute("out", doc.Frames(c.In) + lenF - 1), P("window", 75), P("max_gain", "20dB"),
                    P("level", $"0=-60;{doc.Frames(c.AudioFadeIn) - 1}=0"), P("mlt_service", "volume"),
                    P("shotcut:filter", "fadeInVolume"), P("shotcut:animIn", doc.Frames(c.AudioFadeIn))));
            if (c.AudioFadeOut > 0)
                el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), new XAttribute("in", doc.Frames(c.In)),
                    new XAttribute("out", doc.Frames(c.In) + lenF - 1), P("window", 75), P("max_gain", "20dB"),
                    P("level", $"{lenF - doc.Frames(c.AudioFadeOut)}=0;{lenF - 1}=-60"), P("mlt_service", "volume"),
                    P("shotcut:filter", "fadeOutVolume"), P("shotcut:animOut", doc.Frames(c.AudioFadeOut))));
        }
        if (t.Type != "audio")
        {
            // size / position / rotation / opacity / motion
            bool needAffine = c.Kind != "text" && (c.Scale != 1 || c.X != 0 || c.Y != 0 || c.Rotation != 0 || c.Opacity != 1 || c.Motion != null);
            if (needAffine)
            {
                string Rect(double scale, double x, double y)
                {
                    double w = W * scale, h = H * scale;
                    double rx = (W - w) / 2 + x * W / 2, ry = (H - h) / 2 - y * H / 2;
                    return string.Format(C, "{0:0.##} {1:0.##} {2:0.##} {3:0.##} {4:0.###}", rx, ry, w, h, c.Opacity);
                }
                string rect;
                if (c.Motion is { } m)
                {
                    double z = 1 + c.MotionStrength, hs = c.MotionStrength / 2, s = c.Scale;
                    (double s0, double x0, double y0, double s1, double x1, double y1) = m switch
                    {
                        "zoom_in" => (s, c.X, c.Y, s * z, c.X, c.Y),
                        "zoom_out" => (s * z, c.X, c.Y, s, c.X, c.Y),
                        "pan_left" => (s * z, c.X + hs, c.Y, s * z, c.X - hs, c.Y),
                        "pan_right" => (s * z, c.X - hs, c.Y, s * z, c.X + hs, c.Y),
                        "pan_up" => (s * z, c.X, c.Y - hs, s * z, c.X, c.Y + hs),
                        "pan_down" => (s * z, c.X, c.Y + hs, s * z, c.X, c.Y - hs),
                        _ => (s, c.X, c.Y, s, c.X, c.Y),
                    };
                    rect = $"0={Rect(s0, x0, y0)};{lenF - 1}={Rect(s1, x1, y1)}";
                }
                else rect = Rect(c.Scale, c.X, c.Y);
                el.Add(new XElement("filter", new XAttribute("id", NewId("filter")),
                    P("background", "color:#00000000"), P("mlt_service", "affine"), P("shotcut:filter", "affineSizePosition"),
                    P("transition.fill", 1), P("transition.distort", 0), P("transition.rect", rect), P("transition.valign", "middle"),
                    P("transition.halign", "center"), P("transition.threads", 0), P("transition.fix_rotate_x", c.Rotation)));
            }
            if (c.VideoFadeIn > 0)
                el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), P("start", 1),
                    P("level", $"0=0;{doc.Frames(c.VideoFadeIn) - 1}=1"), P("mlt_service", "brightness"),
                    P("shotcut:filter", "fadeInBrightness"), P("alpha", 1), P("shotcut:animIn", doc.Frames(c.VideoFadeIn))));
            if (c.VideoFadeOut > 0)
                el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), P("start", 1),
                    P("level", $"{lenF - doc.Frames(c.VideoFadeOut)}=1;{lenF - 1}=0"), P("mlt_service", "brightness"),
                    P("shotcut:filter", "fadeOutBrightness"), P("alpha", 1), P("shotcut:animOut", doc.Frames(c.VideoFadeOut))));
            if (c.FlipH) el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), P("mlt_service", "mirror"),
                P("mirror", "flip"), P("reverse", 0), P("shotcut:filter", "mirror")));
            if (c.FlipV) el.Add(new XElement("filter", new XAttribute("id", NewId("filter")), P("mlt_service", "mirror"),
                P("mirror", "flop"), P("reverse", 0), P("shotcut:filter", "flip")));
        }
        if (c.Extra.TryGetValue("mltFilters", out var raw))
            foreach (var f in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                try
                {
                    var fe = XElement.Parse(f);
                    fe.SetAttributeValue("id", NewId("filter"));
                    el.Add(fe);
                }
                catch { }
        return (el, id);
    }

    static int Gcd(int a, int b) => b == 0 ? Math.Max(a, 1) : Gcd(b, a % b);
}
