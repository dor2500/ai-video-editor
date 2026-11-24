using System.Globalization;
using System.Security;
using System.Text.Json.Nodes;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core.Formats;

/// <summary>
/// OpenShot project (.osp, JSON). Each clip's original JSON is kept and only the modelled fields are rewritten,
/// so unknown settings survive. Titles become SVG files in a "Titles" folder next to the project.
/// Transitions are written as fade-through-black (alpha keyframes); OpenShot's own wipes need its UI.
/// </summary>
public sealed class OpenShotFormat : ITimelineFormat
{
    public string Extension => ".osp";
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public TimelineDoc Load(string path)
    {
        var o = Json.Load(path)!.AsObject();
        var doc = new TimelineDoc
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Width = (int)o.Long("width", 1920), Height = (int)o.Long("height", 1080),
        };
        var fps = o["fps"];
        doc.Fps = fps.Dbl("num", 30) / Math.Max(fps.Dbl("den", 1), 1);
        var layers = (o["layers"]?.AsArray() ?? []).Select(l => l!.AsObject()).OrderByDescending(l => l.Long("number")).ToList();
        var byLayer = new Dictionary<long, TTrack>();
        foreach (var l in layers)
        {
            var t = new TTrack { Type = "video", Name = l.Str("label") };
            t.Extra["osp"] = l.ToJsonString();
            byLayer[l.Long("number")] = t;
            doc.Tracks.Add(t);
        }
        foreach (var cj in o["clips"]?.AsArray() ?? [])
        {
            var c = cj!.AsObject();
            var layer = c.Long("layer");
            if (!byLayer.TryGetValue(layer, out var t))
            {
                t = new TTrack { Type = "video" };
                byLayer[layer] = t;
                doc.Tracks.Add(t);
            }
            var reader = c["reader"];
            var file = reader.Str("path");
            bool hasVideo = reader.Bool("has_video", true) && !file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            bool single = reader.Bool("has_single_image");
            double st = c.Dbl("start"), en = c.Dbl("end");
            var clip = new TClip
            {
                Id = c.Str("id") is { Length: > 0 } id ? id : Ids.Short(),
                Path = file.Replace('/', '\\'), Start = T.Us(c.Dbl("position")), Dur = T.Us(en - st), In = T.Us(st),
                SourceDur = single ? 0 : T.Us(reader.Dbl("duration")), HasAudio = reader.Bool("has_audio"),
                Kind = !hasVideo ? "audio" : single ? (c.Str("title").StartsWith("Title:") || file.EndsWith(".svg") ? "text" : "photo") : "video",
                MediaWidth = (int)reader.Long("width"), MediaHeight = (int)reader.Long("height"),
                X = First(c["location_x"], 0) * 2, Y = -First(c["location_y"], 0) * 2, Scale = First(c["scale_x"], 1),
                Rotation = First(c["rotation"], 0), Opacity = First(c["alpha"], 1), Volume = First(c["volume"], 1),
            };
            if (clip.Kind == "text") clip.Text = c.Str("title").Replace("Title: ", "");
            clip.Extra["osp"] = c.ToJsonString();
            t.Clips.Add(clip);
        }
        foreach (var t in doc.Tracks)
        {
            t.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (t.Clips.Count > 0 && t.Clips.All(x => x.Kind == "audio")) t.Type = "audio";
            else if (t.Clips.Count > 0 && t.Clips.All(x => x.Kind == "text")) t.Type = "text";
        }
        // main (bottom) video track first
        var bottom = doc.Tracks.LastOrDefault(t => t.Type == "video");
        if (bottom != null) { doc.Tracks.Remove(bottom); doc.Tracks.Insert(0, bottom); }
        return doc;
    }

    static double First(JsonNode? kf, double def)
    {
        var pts = kf?["Points"]?.AsArray();
        return pts is { Count: > 0 } ? pts[0]?["co"].Dbl("Y", def) ?? def : def;
    }

    static JsonObject Kf(params (double frame, double value)[] pts) => new()
    {
        ["Points"] = new JsonArray(pts.Select(p => (JsonNode)new JsonObject
        {
            ["co"] = new JsonObject { ["X"] = p.frame, ["Y"] = p.value }, ["interpolation"] = 1,
        }).ToArray()),
    };

    public void Save(TimelineDoc doc, string path)
    {
        JsonObject o;
        try { o = File.Exists(path) ? Json.Load(path)!.AsObject() : Skeleton(doc); }
        catch { o = Skeleton(doc); }
        o["width"] = doc.Width;
        o["height"] = doc.Height;
        o["fps"] = new JsonObject { ["num"] = doc.IntFps, ["den"] = 1 };
        int g = Gcd(doc.Width, doc.Height);
        o["display_ratio"] = new JsonObject { ["num"] = doc.Width / g, ["den"] = doc.Height / g };
        o["duration"] = Math.Max(T.Sec(doc.Duration) + 10, 300);
        // layers: OpenShot draws higher numbers on top. Main track = lowest.
        var visual = doc.Tracks.Where(t => t.Type != "audio").ToList();
        var audio = doc.Tracks.Where(t => t.Type == "audio").ToList();
        var ordered = audio.Concat(visual).ToList();
        var layers = new JsonArray();
        var layerNum = new Dictionary<TTrack, long>();
        for (int i = 0; i < ordered.Count; i++)
        {
            long num = (i + 1) * 1_000_000L;
            layerNum[ordered[i]] = num;
            JsonObject l = ordered[i].Extra.TryGetValue("osp", out var lj) ? JsonNode.Parse(lj)!.AsObject() : new JsonObject();
            l["id"] = l.Str("id") is { Length: > 0 } lid ? lid : "L" + (i + 1);
            l["number"] = num;
            l["y"] = 0;
            l["label"] = ordered[i].Name;
            l["lock"] = l.Bool("lock");
            layers.Add(l);
        }
        o["layers"] = layers;
        var files = o.Arr("files");
        var fileIds = files.Where(f => f != null).ToDictionary(f => f.Str("path").Replace('\\', '/'), f => f.Str("id"), StringComparer.OrdinalIgnoreCase);
        var clips = new JsonArray();
        double fps = doc.IntFps;
        var titlesDir = Path.Combine(Path.GetDirectoryName(path)!, "Titles");
        foreach (var t in ordered)
        {
            var list = t.Clips.OrderBy(c => c.Start).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var file = c.Path;
                if (c.Kind == "text") file = WriteTitle(doc, c, titlesDir);
                var norm = file.Replace('\\', '/');
                if (!fileIds.TryGetValue(norm, out var fid))
                {
                    fid = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                    var fr = Reader(c, norm);
                    fr["id"] = fid;
                    fr["media_type"] = c.Kind is "photo" or "text" ? "image" : c.Kind == "audio" ? "audio" : "video";
                    files.Add(fr);
                    fileIds[norm] = fid;
                }
                JsonObject cj = c.Extra.TryGetValue("osp", out var raw) ? JsonNode.Parse(raw)!.AsObject() : NewClip();
                double st = T.Sec(c.In), en = st + T.Sec(c.Dur);
                cj["id"] = c.Id;
                cj["layer"] = layerNum[t];
                cj["position"] = T.Sec(c.Start);
                cj["start"] = st;
                cj["end"] = en;
                cj["duration"] = c.Kind is "photo" or "text" ? 3600.0 : T.Sec(c.SourceDur);
                cj["file_id"] = fid;
                cj["title"] = c.Kind == "text" ? "Title: " + c.Text : Path.GetFileName(file);
                if (cj["reader"] is not JsonObject || cj["reader"].Str("path").Replace('\\', '/') != norm) cj["reader"] = Reader(c, norm);
                cj["location_x"] = Kf((1, c.X / 2));
                cj["location_y"] = Kf((1, -c.Y / 2));
                double f0 = 1, f1 = Math.Max(1, Math.Round(T.Sec(c.Dur) * fps));
                if (c.Motion is { } m)
                {
                    double z = 1 + c.MotionStrength, h = c.MotionStrength / 2, s = c.Scale;
                    (double s0, double x0, double s1, double x1, double y0, double y1) = m switch
                    {
                        "zoom_in" => (s, c.X, s * z, c.X, c.Y, c.Y), "zoom_out" => (s * z, c.X, s, c.X, c.Y, c.Y),
                        "pan_left" => (s * z, c.X + h, s * z, c.X - h, c.Y, c.Y), "pan_right" => (s * z, c.X - h, s * z, c.X + h, c.Y, c.Y),
                        "pan_up" => (s * z, c.X, s * z, c.X, c.Y - h, c.Y + h), "pan_down" => (s * z, c.X, s * z, c.X, c.Y + h, c.Y - h),
                        _ => (s, c.X, s, c.X, c.Y, c.Y),
                    };
                    cj["scale_x"] = Kf((f0, s0), (f1, s1));
                    cj["scale_y"] = Kf((f0, s0), (f1, s1));
                    cj["location_x"] = Kf((f0, x0 / 2), (f1, x1 / 2));
                    cj["location_y"] = Kf((f0, -y0 / 2), (f1, -y1 / 2));
                }
                else
                {
                    cj["scale_x"] = Kf((1, c.Scale));
                    cj["scale_y"] = Kf((1, c.Scale));
                }
                cj["rotation"] = Kf((1, c.Rotation));
                // alpha: opacity plus fades (a dissolve is written as a short fade out/in)
                var pts = new List<(double, double)>();
                long fin = c.VideoFadeIn, fout = c.VideoFadeOut;
                if (i > 0 && list[i - 1].Transition != null && list[i - 1].End == c.Start) fin = Math.Max(fin, list[i - 1].TransitionDur / 2);
                if (c.Transition != null) fout = Math.Max(fout, c.TransitionDur / 2);
                if (fin > 0) { pts.Add((f0, 0)); pts.Add((f0 + Math.Round(T.Sec(fin) * fps), c.Opacity)); }
                else pts.Add((f0, c.Opacity));
                if (fout > 0) { pts.Add((Math.Max(f0 + 1, f1 - Math.Round(T.Sec(fout) * fps)), c.Opacity)); pts.Add((f1, 0)); }
                cj["alpha"] = Kf(pts.ToArray());
                var vol = new List<(double, double)>();
                if (c.AudioFadeIn > 0) { vol.Add((f0, 0)); vol.Add((f0 + Math.Round(T.Sec(c.AudioFadeIn) * fps), c.Volume)); }
                else vol.Add((f0, c.Volume));
                if (c.AudioFadeOut > 0) { vol.Add((Math.Max(f0 + 1, f1 - Math.Round(T.Sec(c.AudioFadeOut) * fps)), c.Volume)); vol.Add((f1, 0)); }
                cj["volume"] = Kf(vol.ToArray());
                bool muted = t.Muted || t.Type != "audio" && c.Kind == "audio";
                cj["has_audio"] = Kf((1, muted ? 0 : -1));
                cj["has_video"] = Kf((1, t.Type == "audio" ? 0 : -1));
                if (Math.Abs(c.Speed - 1) > 1e-6)
                    cj["time"] = Kf((1, Math.Round(st * fps) + 1), (f1, Math.Round(st * fps + T.Sec(c.Dur) * c.Speed * fps)));
                clips.Add(cj);
            }
        }
        o["clips"] = clips;
        var tmp = path + ".aitmp";
        File.WriteAllText(tmp, o.ToJsonString(Json.Pretty));
        File.Move(tmp, path, true);
    }

    static JsonObject Reader(TClip c, string norm)
    {
        bool img = c.Kind is "photo" or "text";
        return new JsonObject
        {
            ["type"] = img ? "QtImageReader" : "FFmpegReader", ["path"] = norm,
            ["has_video"] = c.Kind != "audio", ["has_audio"] = !img && c.HasAudio, ["has_single_image"] = img,
            ["duration"] = img ? 3600.0 : T.Sec(c.SourceDur), ["width"] = c.MediaWidth > 0 ? c.MediaWidth : 1920,
            ["height"] = c.MediaHeight > 0 ? c.MediaHeight : 1080, ["fps"] = new JsonObject { ["num"] = 30, ["den"] = 1 },
            ["video_timebase"] = new JsonObject { ["num"] = 1, ["den"] = 30 },
            ["pixel_ratio"] = new JsonObject { ["num"] = 1, ["den"] = 1 }, ["sample_rate"] = 44100, ["channels"] = 2,
            ["channel_layout"] = 3, ["video_length"] = img ? "108000" : ((long)(T.Sec(c.SourceDur) * 30)).ToString(C),
        };
    }

    static JsonObject NewClip() => new()
    {
        ["anchor"] = 0, ["gravity"] = 4, ["scale"] = 1, ["display"] = 0, ["mixing"] = 0, ["waveform"] = false,
        ["effects"] = new JsonArray(), ["channel_filter"] = Kf((1, -1)), ["channel_mapping"] = Kf((1, -1)),
        ["crop_x"] = Kf((1, 0)), ["crop_y"] = Kf((1, 0)), ["crop_width"] = Kf((1, 1)), ["crop_height"] = Kf((1, 1)),
        ["shear_x"] = Kf((1, 0)), ["shear_y"] = Kf((1, 0)), ["origin_x"] = Kf((1, 0.5)), ["origin_y"] = Kf((1, 0.5)),
        ["wave_color"] = new JsonObject { ["red"] = Kf((1, 0)), ["green"] = Kf((1, 123)), ["blue"] = Kf((1, 255)), ["alpha"] = Kf((1, 255)) },
    };

    static JsonObject Skeleton(TimelineDoc doc) => new()
    {
        ["id"] = "T0", ["fps"] = new JsonObject { ["num"] = doc.IntFps, ["den"] = 1 },
        ["pixel_ratio"] = new JsonObject { ["num"] = 1, ["den"] = 1 }, ["width"] = doc.Width, ["height"] = doc.Height,
        ["sample_rate"] = 44100, ["channels"] = 2, ["channel_layout"] = 3, ["settings"] = new JsonObject(),
        ["clips"] = new JsonArray(), ["effects"] = new JsonArray(), ["export_path"] = "", ["files"] = new JsonArray(),
        ["duration"] = 300, ["scale"] = 15.0, ["tick_pixels"] = 100, ["playhead_position"] = 0,
        ["profile"] = $"{doc.Width}x{doc.Height} {doc.IntFps}fps", ["layers"] = new JsonArray(), ["markers"] = new JsonArray(),
        ["progress"] = new JsonArray(), ["history"] = new JsonObject { ["undo"] = new JsonArray(), ["redo"] = new JsonArray() },
        ["version"] = new JsonObject { ["openshot-qt"] = "3.1.1", ["libopenshot"] = "0.3.2" },
    };

    static string WriteTitle(TimelineDoc doc, TClip c, string dir)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"title_{c.Id}.svg");
        double px = c.FontSize * 8 * (doc.Height / 1080.0);
        double x = (c.X + 1) / 2 * doc.Width, y = (1 - c.Y) / 2 * doc.Height;
        var anchor = c.Align == "left" ? "start" : c.Align == "right" ? "end" : "middle";
        var stroke = c.StrokeColor != null ? $" stroke=\"{c.StrokeColor}\" stroke-width=\"{px / 16:0.#}\" paint-order=\"stroke\"" : "";
        var bg = c.BackgroundColor != null
            ? $"<rect x=\"0\" y=\"{(y - px * 0.9).ToString("0", C)}\" width=\"{doc.Width}\" height=\"{(px * 1.4).ToString("0", C)}\" fill=\"{c.BackgroundColor}\" fill-opacity=\"0.6\"/>"
            : "";
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{doc.Width}" height="{doc.Height}" viewBox="0 0 {doc.Width} {doc.Height}">
              {bg}
              <text x="{x.ToString("0", C)}" y="{y.ToString("0", C)}" font-family="{SecurityElement.Escape(c.Font ?? "Arial")}" font-size="{px.ToString("0", C)}" font-weight="{(c.Bold ? "bold" : "normal")}" font-style="{(c.Italic ? "italic" : "normal")}" fill="{c.Color}" text-anchor="{anchor}" dominant-baseline="middle"{stroke}>{SecurityElement.Escape(c.Text ?? "")}</text>
            </svg>
            """;
        File.WriteAllText(file, svg);
        c.MediaWidth = doc.Width;
        c.MediaHeight = doc.Height;
        return file;
    }

    static int Gcd(int a, int b) => b == 0 ? Math.Max(a, 1) : Gcd(b, a % b);
}
