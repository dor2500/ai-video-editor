using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIVideoEditor.Core.Timeline;

public interface ITimelineFormat
{
    string Extension { get; }
    TimelineDoc Load(string path);
    void Save(TimelineDoc doc, string path);
}

/// <summary>Our own project file (.aive.json) – the source of truth for export-only editors.</summary>
public sealed class NativeFormat : ITimelineFormat
{
    public string Extension => ".aive.json";
    public TimelineDoc Load(string path) => JsonSerializer.Deserialize<TimelineDoc>(File.ReadAllText(path), Json.Pretty) ?? new TimelineDoc();
    public void Save(TimelineDoc doc, string path) => Json.SaveAtomic(path, JsonSerializer.Serialize(doc, Json.Pretty));
}

/// <summary>
/// A project edited through the neutral timeline. Either the editor's own file is loaded/saved directly
/// (Shotcut .mlt, OpenShot .osp), or our .aive.json is the source and import files are exported on every save
/// (Premiere, DaVinci Resolve, Final Cut, Vegas, Kdenlive, ...).
/// </summary>
public sealed class TimelineProject : EditorProject
{
    public static readonly IReadOnlySet<string> Tools = new HashSet<string>
    {
        "add_media", "add_audio", "add_text", "edit_segments", "move_segment", "delete_segments", "split_segment",
        "reorder_main_track", "close_gaps", "set_track_mute", "set_track_hidden", "set_canvas", "set_transitions", "set_animations",
        "add_motion", "set_audio_fade", "search_catalog",
        "set_adjustments", "duplicate_segment", "reverse_segment", "freeze_frame", "extract_audio", "replace_media", "set_keyframes", "import_srt",
    };

    readonly EditorDef editor;
    readonly string file;
    readonly ITimelineFormat format;
    public TimelineDoc Doc { get; private set; } = new();
    public List<string> LastExports { get; } = [];

    public TimelineProject(EditorDef editor, string file)
    {
        this.editor = editor;
        this.file = file;
        format = editor.DirectFormat ?? new NativeFormat();
        if (File.Exists(file)) Reload();
        else
        {
            Doc = new TimelineDoc { Name = BaseName };
            Save();
        }
    }

    public override string EditorId => editor.Id;
    public override string MainFile => file;
    public override IReadOnlySet<string> SupportedTools => Tools;
    public override string Name => Doc.Name is { Length: > 0 } n ? n : BaseName;
    string BaseName => Path.GetFileName(file).Replace(".aive.json", "").Replace(Path.GetExtension(file), "");

    public override string Notes => editor.DirectFormat != null
        ? $"{editor.Display}: the project file is edited directly. Effects this app does not understand may be dropped when " +
          "saving (a backup is always kept). Transitions: Cross Dissolve, Dip to Black, Dip to White, Wipe. Animations: Fade In / Fade Out."
        : $"{editor.Display}: the app keeps its own timeline and writes import files ({string.Join(", ", editor.Exports.Select(e => e.Extension))}) " +
          $"next to it on every change. The user imports them in {editor.Display} ({editor.ImportHint}). " +
          "Transitions: Cross Dissolve, Dip to Black, Dip to White, Wipe. Animations: Fade In / Fade Out. No CapCut effects/filters.";

    public override void Reload() => Doc = format.Load(file);

    public override void Save()
    {
        format.Save(Doc, file);
        LastExports.Clear();
        if (editor.DirectFormat == null)
        {
            var baseNoExt = file.EndsWith(".aive.json", StringComparison.OrdinalIgnoreCase) ? file[..^10] : Path.ChangeExtension(file, null);
            foreach (var ex in editor.Exports)
            {
                var path = baseNoExt + ex.Extension;
                ex.Save(Doc, path);
                LastExports.Add(path);
            }
        }
    }

    public override string? AfterSaveMessage => LastExports.Count > 0 ? "Import files updated: " + string.Join(", ", LastExports.Select(Path.GetFileName)) : null;

    // ------------------------------------------------------------------ helpers
    List<TTrack> TracksOf(string type) => Doc.Tracks.Where(t => t.Type == type).ToList();

    TTrack AddTrack(string type)
    {
        var t = new TTrack { Type = type };
        // keep order: video tracks first, then text, then audio (matters for MLT/XML writers)
        int order(string ty) => ty switch { "video" => 0, "text" => 1, _ => 2 };
        var idx = Doc.Tracks.FindLastIndex(x => order(x.Type) <= order(type));
        Doc.Tracks.Insert(idx + 1, t);
        return t;
    }

    TTrack MainTrack() => TracksOf("video").FirstOrDefault() ?? AddTrack("video");

    static bool FreeAt(TTrack t, long start, long dur) => t.Clips.All(c => !(start < c.End && c.Start < start + dur));
    static long TrackEnd(TTrack t) => t.Clips.Select(c => c.End).DefaultIfEmpty(0).Max();
    static void Sort(TTrack t) => t.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));

    TTrack PickTrack(string type, long start, long dur, bool newTrack = false)
    {
        if (!newTrack)
            foreach (var t in TracksOf(type).Skip(type == "video" ? 1 : 0))
                if (FreeAt(t, start, dur)) return t;
        return AddTrack(type);
    }

    (TTrack track, TClip clip, int index) Find(string id)
    {
        foreach (var t in Doc.Tracks)
            for (int i = 0; i < t.Clips.Count; i++)
                if (t.Clips[i].Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)) return (t, t.Clips[i], i);
        throw new EditException($"Segment '{id}' not found. Call get_project_overview for ids.");
    }

    TTrack ByLabel(string label)
    {
        var p = label.Split('#');
        var same = TracksOf(p[0]);
        int i = p.Length > 1 && int.TryParse(p[1], out var n) ? n : 0;
        if (i >= same.Count) throw new EditException($"No track {label}");
        return same[i];
    }

    bool IsMain(TTrack t) => TracksOf("video").FirstOrDefault() == t;

    List<string> IdsOrAllMain(Args a, bool photosOnly = false)
    {
        var ids = a.L("ids");
        if (a.B("all_main", false) || a.B("all_main_photos", false))
            ids.AddRange(MainTrack().Clips.Where(c => !photosOnly || c.Kind == "photo").Select(c => c.Id));
        if (ids.Count == 0) throw new EditException("No segment ids given");
        return ids;
    }

    // ------------------------------------------------------------------ overview
    public override JsonNode Overview()
    {
        var tracks = new JsonArray();
        var counters = new Dictionary<string, int>();
        var pool = new JsonArray();
        var seenPool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Doc.Tracks)
        {
            var i = counters.GetValueOrDefault(t.Type);
            counters[t.Type] = i + 1;
            bool isMain = t.Type == "video" && i == 0;
            foreach (var c in t.Clips)
            {
                if (!string.IsNullOrEmpty(c.Path) && seenPool.Add(c.Path))
                {
                    pool.Add(new JsonObject
                    {
                        ["path"] = c.Path, ["name"] = c.FileName, ["kind"] = c.Kind,
                        ["duration_s"] = T.Sec(c.SourceDur > 0 ? c.SourceDur : c.Dur),
                        ["width"] = c.MediaWidth, ["height"] = c.MediaHeight, ["in_timeline"] = true,
                    });
                }
            }
            tracks.Add(new JsonObject
            {
                ["track"] = $"{t.Type}#{i}" + (isMain ? " (main)" : ""),
                ["type"] = t.Type, ["index"] = i, ["is_main"] = isMain,
                ["muted"] = t.Muted, ["hidden"] = t.Hidden,
                ["segments"] = new JsonArray(t.Clips.Select(c => (JsonNode)Describe(c)).ToArray()),
            });
        }
        return new JsonObject
        {
            ["editor"] = editor.Display, ["project"] = Name, ["duration_s"] = T.Sec(Doc.Duration),
            ["canvas"] = $"{Doc.Width}x{Doc.Height}", ["width"] = Doc.Width, ["height"] = Doc.Height,
            ["ratio"] = T.Ratio(Doc.Width, Doc.Height), ["fps"] = Doc.Fps,
            ["can_undo"] = CanUndo, ["can_redo"] = CanRedo, ["media_pool"] = pool, ["tracks"] = tracks,
        };
    }

    static JsonObject Describe(TClip c)
    {
        var o = new JsonObject
        {
            ["id"] = c.Id[..Math.Min(8, c.Id.Length)], ["start"] = T.Sec(c.Start), ["end"] = T.Sec(c.End), ["dur"] = T.Sec(c.Dur),
            ["kind"] = c.Kind, ["path"] = c.Path, ["source_in"] = T.Sec(c.In), ["source_dur"] = T.Sec(c.SourceDur),
            ["visible"] = c.Opacity > 0, ["flip_h"] = c.FlipH, ["flip_v"] = c.FlipV,
        };
        if (c.Kind == "text")
        {
            o["text"] = c.Text;
            o["font_size"] = c.FontSize;
            o["color"] = c.Color;
            o["bold"] = c.Bold;
            o["italic"] = c.Italic;
            o["align"] = c.Align;
            if (!string.IsNullOrEmpty(c.Font)) o["font"] = c.Font;
            if (!string.IsNullOrEmpty(c.StrokeColor)) { o["stroke_color"] = c.StrokeColor; o["stroke_width"] = c.StrokeWidth; }
            if (!string.IsNullOrEmpty(c.BackgroundColor)) { o["background_color"] = c.BackgroundColor; o["background_alpha"] = c.BackgroundAlpha; }
            if (c.Shadow) o["shadow"] = true;
            if (c.LetterSpacing != 0) o["letter_spacing"] = c.LetterSpacing;
            if (c.LineSpacing != 0.02) o["line_spacing"] = c.LineSpacing;
        }
        else if (c.FileName != "") o["file"] = c.FileName;
        if (c.X != 0 || c.Y != 0) o["pos"] = new JsonArray(Math.Round(c.X, 3), Math.Round(c.Y, 3));
        if (c.Scale != 1) o["scale"] = Math.Round(c.Scale, 3);
        if (c.Rotation != 0) o["rot"] = c.Rotation;
        if (c.Opacity != 1) o["opacity"] = c.Opacity;
        if (c.Volume != 1 && c.Kind is "video" or "audio") o["volume"] = c.Volume;
        if (c.Speed != 1) o["speed"] = c.Speed;
        if (c.Reverse) o["reverse"] = true;
        if (c.KeepPitch) o["keep_pitch"] = true;
        if (c.BlendMode != "normal") o["blend_mode"] = c.BlendMode;
        if (c.CanvasBlur > 0) o["canvas_blur"] = c.CanvasBlur;
        if (!string.IsNullOrEmpty(c.CanvasColor)) o["canvas_color"] = c.CanvasColor;
        if (c.CornerRadius > 0) o["corner_radius"] = c.CornerRadius;
        if (c.CropLeft > 0 || c.CropRight > 0 || c.CropTop > 0 || c.CropBottom > 0)
            o["crop"] = new JsonArray(c.CropLeft, c.CropRight, c.CropTop, c.CropBottom);
        if (c.AudioPan != 0) o["audio_pan"] = c.AudioPan;
        if (c.NoiseReduction) o["noise_reduction"] = true;
        if (c.VocalIsolation != "none") o["vocal_isolation"] = c.VocalIsolation;
        if (c.Brightness != 0 || c.Contrast != 0 || c.Saturation != 0 || c.Temperature != 0 || c.Hue != 0 ||
            c.Sharpen != 0 || c.Highlights != 0 || c.Shadows != 0 || c.Vignette != 0 || c.ColorFade != 0)
        {
            o["adjust"] = new JsonObject
            {
                ["brightness"] = c.Brightness, ["contrast"] = c.Contrast, ["saturation"] = c.Saturation,
                ["temperature"] = c.Temperature, ["hue"] = c.Hue, ["sharpen"] = c.Sharpen,
                ["highlights"] = c.Highlights, ["shadows"] = c.Shadows, ["vignette"] = c.Vignette, ["fade"] = c.ColorFade,
            };
        }
        if (c.Keyframes.Count > 0)
        {
            var kfs = new JsonArray();
            foreach (var k in c.Keyframes)
            {
                var ko = new JsonObject { ["t"] = T.Sec(k.TimeUs) };
                if (k.Scale is { } ks) ko["scale"] = ks;
                if (k.X is { } kx) ko["x"] = kx;
                if (k.Y is { } ky) ko["y"] = ky;
                if (k.Rotation is { } kr) ko["rot"] = kr;
                if (k.Opacity is { } kp) ko["opacity"] = kp;
                kfs.Add(ko);
            }
            o["keyframes"] = kfs;
        }
        if (c.Extra.ContainsKey("freeze")) o["freeze"] = true;
        if (c.AudioFadeIn > 0) o["fade_in"] = T.Sec(c.AudioFadeIn);
        if (c.AudioFadeOut > 0) o["fade_out"] = T.Sec(c.AudioFadeOut);
        if (c.Transition != null) { o["transition"] = c.Transition; o["transition_dur"] = T.Sec(c.TransitionDur); }
        if (c.Motion != null) o["motion"] = c.Motion;
        var fx = new JsonArray();
        if (c.Transition != null) fx.Add($"transition:{c.Transition}");
        if (c.VideoFadeIn > 0) fx.Add("anim-in:Fade In");
        if (c.VideoFadeOut > 0) fx.Add("anim-out:Fade Out");
        if (c.AudioFadeIn > 0 || c.AudioFadeOut > 0) fx.Add($"fade:{T.Sec(c.AudioFadeIn)}/{T.Sec(c.AudioFadeOut)}");
        if (c.Motion != null) fx.Add($"motion:{c.Motion}");
        if (c.Keyframes.Count > 0) fx.Add("keyframes");
        if (fx.Count > 0) o["fx"] = fx;
        return o;
    }

    JsonObject DescribeId(string id) => Describe(Find(id).clip);

    // ------------------------------------------------------------------ dispatch
    public override JsonNode? Run(string tool, Args a)
    {
        switch (tool)
        {
            case "add_media": return AddMedia(a);
            case "add_audio": return AddAudio(a);
            case "add_text": return AddText(a);
            case "edit_segments": return new JsonArray(a.L("ids").Select(id => (JsonNode)Edit(id, a)).ToArray());
            case "move_segment": return Move(a.Req("id"), a.D("start"), a.S("to_track"));
            case "delete_segments": return Obj(new { deleted = Delete(a.L("ids"), a.B("ripple", true)) });
            case "split_segment": return Split(a.Req("id"), a.ReqD("at"));
            case "reorder_main_track": return Reorder(a.L("order"));
            case "close_gaps": return Obj(new { moved = CloseGaps(a.S("track", "video#0")) });
            case "set_track_mute":
                var t = ByLabel(a.Req("track"));
                t.Muted = a.B("muted", true);
                return "ok";
            case "set_track_hidden":
                var th = ByLabel(a.Req("track"));
                th.Hidden = a.B("hidden", true);
                return "ok";
            case "set_canvas": return SetCanvas(a.Req("ratio"));
            case "search_catalog": return Search(a.Req("category"), a.S("query", ""));
            case "set_transitions": return SetTransitions(a);
            case "set_animations": return SetAnimations(a);
            case "add_motion": return AddMotion(a);
            case "set_audio_fade":
                foreach (var id in a.L("ids"))
                {
                    var c = Find(id).clip;
                    c.AudioFadeIn = T.Us(a.D("fade_in", 0));
                    c.AudioFadeOut = T.Us(a.D("fade_out", 0));
                }
                return "ok";
            case "set_adjustments": return SetAdjustments(a);
            case "duplicate_segment": return DuplicateSegment(a.Req("id"));
            case "reverse_segment": return ReverseSegment(a.Req("id"), a.B("reversed"));
            case "freeze_frame": return FreezeFrame(a.Req("id"), a.ReqD("at"), a.D("duration", 3.0));
            case "extract_audio": return ExtractAudio(a.Req("id"));
            case "replace_media": return ReplaceMedia(a.Req("id"), a.Req("path"));
            case "set_keyframes": return SetKeyframes(a);
            case "import_srt": return ImportSrt(a);
            default: throw new EditException($"'{tool}' is not supported for {editor.Display}");
        }
    }

    static JsonNode Search(string cat, string q)
    {
        var src = cat switch
        {
            "transition" => GenericCatalog.Transitions,
            "video_in" or "text_in" => [GenericCatalog.Animations[0]],
            "video_out" or "text_out" => [GenericCatalog.Animations[1]],
            _ => [],
        };
        var res = new JsonArray();
        foreach (var (n, d) in src)
            if (q == "" || n.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Contains(q, StringComparison.OrdinalIgnoreCase))
                res.Add(new JsonObject { ["name"] = n, ["description"] = d });
        if (src.Length == 0) res.Add(new JsonObject { ["note"] = $"'{cat}' is not available for this editor." });
        else if (res.Count == 0) foreach (var (n, d) in src) res.Add(new JsonObject { ["name"] = n, ["description"] = d });
        return res;
    }

    // ------------------------------------------------------------------ ops
    JsonNode AddMedia(Args a)
    {
        var target = a.S("target", "main");
        var files = Media.ExpandPaths(a.L("paths"), true).ToList();
        if (a.B("sort_by_name", true)) files.Sort((x, y) => string.Compare(Path.GetFileName(x), Path.GetFileName(y), StringComparison.OrdinalIgnoreCase));
        if (files.Count == 0) throw new EditException("No images/videos found in the given paths");
        var main = target == "main" ? MainTrack() : null;
        long cursor = a.D("start") is { } st ? T.Us(st) : main != null ? TrackEnd(main) : 0;
        var added = new List<TClip>();
        foreach (var f in files)
        {
            var pr = Media.Probe(f);
            if (pr.Kind == MediaKind.Audio) throw new EditException($"{f} is audio; use add_audio");
            long dur = pr.Kind == MediaKind.Photo ? T.Us(a.D("photo_duration", 3)) : pr.DurationUs;
            if (pr.Kind == MediaKind.Video && a.D("max_video_duration") is { } mx) dur = Math.Min(dur, T.Us(mx));
            var c = new TClip
            {
                Kind = pr.Kind == MediaKind.Photo ? "photo" : "video", Path = Path.GetFullPath(f), Start = cursor, Dur = dur,
                SourceDur = pr.Kind == MediaKind.Photo ? 0 : pr.DurationUs, MediaWidth = pr.Width, MediaHeight = pr.Height,
                HasAudio = pr.HasAudio, Scale = target == "overlay" ? 0.5 : 1,
            };
            TTrack tr;
            if (main != null)
            {
                tr = main;
                if (!FreeAt(tr, cursor, dur))
                    foreach (var o in tr.Clips.Where(o => o.Start >= cursor)) o.Start += dur;
                cursor += dur;
            }
            else tr = PickTrack("video", c.Start, dur, target == "new_track");
            tr.Clips.Add(c);
            Sort(tr);
            added.Add(c);
        }
        return new JsonArray(added.Select(c => (JsonNode)Describe(c)).ToArray());
    }

    JsonNode AddAudio(Args a)
    {
        var path = Paths.Expand(a.Req("path"));
        var pr = Media.Probe(path);
        if (pr.Kind == MediaKind.Photo) throw new EditException("That's an image, not audio");
        long st = T.Us(a.D("start", 0)), src0 = T.Us(a.D("trim_start", 0)), avail = pr.DurationUs - src0;
        if (avail <= 0) throw new EditException("trim_start is beyond the end of the audio");
        bool fit = a.B("fit_to_video", false), loop = a.B("loop", false);
        long total;
        if (fit)
        {
            total = TracksOf("video").Select(TrackEnd).DefaultIfEmpty(0).Max() - st;
            if (total <= 0) throw new EditException("There is no video to fit the audio to");
        }
        else total = a.D("duration") is { } du ? T.Us(du) : avail;
        var pieces = new List<(long t, long d)>();
        if (loop || fit) for (long rem = total, t = st; rem > 0;) { var dd = Math.Min(rem, avail); pieces.Add((t, dd)); t += dd; rem -= dd; }
        else pieces.Add((st, Math.Min(total, avail)));
        var tr = PickTrack("audio", pieces[0].t, pieces[^1].t + pieces[^1].d - pieces[0].t, a.B("new_track", false));
        var res = new JsonArray();
        for (int i = 0; i < pieces.Count; i++)
        {
            var c = new TClip
            {
                Kind = pr.Kind == MediaKind.Video ? "audio" : "audio", Path = path, Start = pieces[i].t, Dur = pieces[i].d, In = src0,
                SourceDur = pr.DurationUs, HasAudio = true, Volume = a.D("volume", 1),
                AudioFadeIn = i == 0 ? T.Us(a.D("fade_in", 0)) : 0,
                AudioFadeOut = i == pieces.Count - 1 ? T.Us(a.D("fade_out", 0)) : 0,
            };
            tr.Clips.Add(c);
            res.Add(Describe(c));
        }
        Sort(tr);
        return res;
    }

    JsonNode AddText(Args a)
    {
        long st = T.Us(a.ReqD("start")), du = T.Us(a.ReqD("duration"));
        var c = new TClip
        {
            Kind = "text", Text = a.Req("text"), Start = st, Dur = du, FontSize = a.D("font_size", 8),
            Color = Colors.Hex(a.S("color", "#FFFFFF")), Bold = a.B("bold", false), Italic = a.B("italic", false),
            X = a.D("x", 0), Y = a.D("y", -0.7), Font = a.S("font"), Align = a.S("align", "center"),
            StrokeColor = a.S("stroke_color") is { Length: > 0 } s ? Colors.Hex(s) : null,
            BackgroundColor = a.S("background_color") is { Length: > 0 } b ? Colors.Hex(b) : null,
        };
        var tr = PickTrack("text", st, du);
        tr.Clips.Add(c);
        Sort(tr);
        return Describe(c);
    }

    JsonObject Edit(string id, Args ch)
    {
        var (t, c, _) = Find(id);
        long oldEnd = c.End;
        if (ch.D("start") is { } st) c.Start = T.Us(st);
        if (ch.D("duration") is { } d)
        {
            long nd = T.Us(d);
            if (c.SourceDur > 0 && c.Kind is "video" or "audio")
            {
                long max = (long)((c.SourceDur - c.In) / c.Speed);
                if (nd > max) throw new EditException($"The clip is only {T.Sec(max)}s long from its trim point");
            }
            c.Dur = nd;
        }
        if (ch.D("x") is { } x) c.X = x;
        if (ch.D("y") is { } y) c.Y = y;
        if (ch.D("scale") is { } sc) c.Scale = sc;
        if (ch.D("rotation") is { } r) c.Rotation = r;
        if (ch.D("opacity") is { } op) c.Opacity = op;
        if (ch.B("flip_horizontal") is { } fh) c.FlipH = fh;
        if (ch.B("flip_vertical") is { } fv) c.FlipV = fv;
        if (ch.D("volume") is { } v) c.Volume = v;
        if (ch.D("speed") is { } sp)
        {
            if (sp <= 0) throw new EditException("speed must be > 0");
            if (c.Kind is "video" or "audio") c.Dur = (long)(c.Dur * c.Speed / sp);
            c.Speed = sp;
        }
        if (ch.B("visible") is { } vis) c.Opacity = vis ? Math.Max(c.Opacity, 1) : 0;
        if (ch.B("reverse") is { } rev) c.Reverse = rev;
        if (ch.B("keep_pitch") is { } kp) c.KeepPitch = kp;
        if (ch.S("blend_mode") is { } bm) c.BlendMode = bm;
        if (ch.D("canvas_blur") is { } cb) c.CanvasBlur = Math.Clamp(cb, 0, 1);
        if (ch.S("canvas_color") is { } cc) c.CanvasColor = cc == "" ? null : Colors.Hex(cc);
        if (ch.D("crop_left") is { } cl) c.CropLeft = Math.Clamp(cl, 0, 0.9);
        if (ch.D("crop_right") is { } cr) c.CropRight = Math.Clamp(cr, 0, 0.9);
        if (ch.D("crop_top") is { } ct) c.CropTop = Math.Clamp(ct, 0, 0.9);
        if (ch.D("crop_bottom") is { } cbo) c.CropBottom = Math.Clamp(cbo, 0, 0.9);
        if (ch.D("corner_radius") is { } crd) c.CornerRadius = Math.Clamp(crd, 0, 1);
        if (ch.D("audio_pan") is { } ap) c.AudioPan = Math.Clamp(ap, -1, 1);
        if (ch.B("noise_reduction") is { } nr) c.NoiseReduction = nr;
        if (ch.S("vocal_isolation") is { } vi) c.VocalIsolation = vi;
        if (ch.S("text") is { } txt) c.Text = txt;
        if (ch.D("font_size") is { } fs) c.FontSize = fs;
        if (ch.S("color") is { } col) c.Color = Colors.Hex(col);
        if (ch.B("bold") is { } bo) c.Bold = bo;
        if (ch.B("italic") is { } it) c.Italic = it;
        if (ch.S("align") is { } al) c.Align = al;
        if (ch.S("font") is { } fnt) c.Font = fnt == "" ? null : fnt;
        if (ch.S("stroke_color") is { } sc2) c.StrokeColor = sc2 == "" ? null : Colors.Hex(sc2);
        if (ch.D("stroke_width") is { } sw) c.StrokeWidth = sw;
        if (ch.S("background_color") is { } bg2) c.BackgroundColor = bg2 == "" ? null : Colors.Hex(bg2);
        if (ch.D("background_alpha") is { } bga) c.BackgroundAlpha = Math.Clamp(bga, 0, 1);
        if (ch.B("shadow") is { } sh) c.Shadow = sh;
        if (ch.D("letter_spacing") is { } ls) c.LetterSpacing = ls;
        if (ch.D("line_spacing") is { } lns) c.LineSpacing = lns;
        if (IsMain(t) && c.End != oldEnd && ch.D("start") is null)
            foreach (var o in t.Clips.Where(o => o != c && o.Start >= oldEnd)) o.Start += c.End - oldEnd;
        Sort(t);
        return Describe(c);
    }

    JsonNode SetAdjustments(Args a)
    {
        bool reset = a.B("reset", false);
        var res = new JsonArray();
        foreach (var id in IdsOrAllMain(a))
        {
            var c = Find(id).clip;
            if (reset)
            {
                c.Brightness = c.Contrast = c.Saturation = c.Temperature = c.Hue = 0;
                c.Sharpen = c.Highlights = c.Shadows = c.Vignette = c.ColorFade = 0;
            }
            if (a.D("brightness") is { } br) c.Brightness = Math.Clamp(br, -1, 1);
            if (a.D("contrast") is { } co) c.Contrast = Math.Clamp(co, -1, 1);
            if (a.D("saturation") is { } sa) c.Saturation = Math.Clamp(sa, -1, 1);
            if (a.D("temperature") is { } te) c.Temperature = Math.Clamp(te, -1, 1);
            if (a.D("hue") is { } hu) c.Hue = Math.Clamp(hu, -180, 180);
            if (a.D("sharpen") is { } sh) c.Sharpen = Math.Clamp(sh, 0, 1);
            if (a.D("highlights") is { } hi) c.Highlights = Math.Clamp(hi, -1, 1);
            if (a.D("shadows") is { } sw) c.Shadows = Math.Clamp(sw, -1, 1);
            if (a.D("vignette") is { } vi) c.Vignette = Math.Clamp(vi, 0, 1);
            if (a.D("fade") is { } fa) c.ColorFade = Math.Clamp(fa, 0, 1);
            res.Add(Describe(c));
        }
        return res;
    }

    JsonNode DuplicateSegment(string id)
    {
        var (t, c, idx) = Find(id);
        var copy = JsonSerializer.Deserialize<TClip>(JsonSerializer.Serialize(c))!;
        copy.Id = Ids.Short();
        copy.Start = c.End;
        copy.Transition = null;
        if (IsMain(t))
        {
            foreach (var o in t.Clips.Where(o => o.Start >= c.End)) o.Start += copy.Dur;
            t.Clips.Insert(idx + 1, copy);
        }
        else
        {
            var dest = FreeAt(t, copy.Start, copy.Dur) ? t : PickTrack(t.Type, copy.Start, copy.Dur);
            dest.Clips.Add(copy);
            Sort(dest);
        }
        return Describe(copy);
    }

    JsonNode ReverseSegment(string id, bool? reversed)
    {
        var c = Find(id).clip;
        c.Reverse = reversed ?? !c.Reverse;
        return Describe(c);
    }

    JsonNode FreezeFrame(string id, double atSec, double durSec)
    {
        var (t, c, idx) = Find(id);
        if (durSec <= 0) throw new EditException("Freeze duration must be > 0");
        long atUs = T.Us(atSec), holdUs = T.Us(durSec);
        long insertAt;
        int insertIdx;
        if (atUs > c.Start + 10_000 && atUs < c.End - 10_000)
        {
            Split(c.Id, atSec);
            (_, c, idx) = Find(c.Id);
            insertAt = c.End;
            insertIdx = idx + 1;
        }
        else
        {
            insertAt = c.End;
            insertIdx = idx + 1;
        }
        var hold = JsonSerializer.Deserialize<TClip>(JsonSerializer.Serialize(c))!;
        hold.Id = Ids.Short();
        hold.Start = insertAt;
        hold.Dur = holdUs;
        hold.Speed = 1;
        hold.Transition = null;
        hold.Extra["freeze"] = "true";
        if (IsMain(t))
        {
            foreach (var o in t.Clips.Where(o => o.Start >= insertAt)) o.Start += holdUs;
            t.Clips.Insert(insertIdx, hold);
        }
        else
        {
            var dest = FreeAt(t, hold.Start, hold.Dur) ? t : PickTrack(t.Type, hold.Start, hold.Dur);
            dest.Clips.Add(hold);
            Sort(dest);
        }
        return Describe(hold);
    }

    JsonNode ExtractAudio(string id)
    {
        var (_, c, _) = Find(id);
        if (c.Kind != "video" || string.IsNullOrEmpty(c.Path))
            throw new EditException("Extract audio requires a video clip");
        var tr = PickTrack("audio", c.Start, c.Dur);
        var aud = new TClip
        {
            Kind = "audio", Path = c.Path, Start = c.Start, Dur = c.Dur, In = c.In,
            SourceDur = c.SourceDur, HasAudio = true, Speed = c.Speed,
            Volume = c.Volume > 0 ? c.Volume : 1,
            AudioFadeIn = c.AudioFadeIn, AudioFadeOut = c.AudioFadeOut,
        };
        tr.Clips.Add(aud);
        Sort(tr);
        c.Volume = 0;
        return new JsonObject { ["video"] = Describe(c), ["audio"] = Describe(aud) };
    }

    JsonNode ReplaceMedia(string id, string rawPath)
    {
        var (_, c, _) = Find(id);
        var path = Paths.Expand(rawPath);
        var pr = Media.Probe(path);
        c.Path = Path.GetFullPath(path);
        c.Kind = pr.Kind == MediaKind.Photo ? "photo" : pr.Kind == MediaKind.Audio ? "audio" : "video";
        c.SourceDur = pr.Kind == MediaKind.Photo ? 0 : pr.DurationUs;
        c.MediaWidth = pr.Width;
        c.MediaHeight = pr.Height;
        c.HasAudio = pr.HasAudio;
        c.In = 0;
        if (c.SourceDur > 0 && c.Dur > (long)(c.SourceDur / c.Speed))
            c.Dur = Math.Max(100_000, (long)(c.SourceDur / c.Speed));
        return Describe(c);
    }

    JsonNode SetKeyframes(Args a)
    {
        var c = Find(a.Req("id")).clip;
        var action = a.Req("action").ToLowerInvariant();
        if (action == "clear") { c.Keyframes.Clear(); return Describe(c); }
        long rel = Math.Clamp(T.Us(a.D("at", T.Sec(c.Start))) - c.Start, 0, c.Dur);
        var kf = c.Keyframes.FirstOrDefault(k => Math.Abs(k.TimeUs - rel) < 20_000);
        if (kf == null) { kf = new TKeyframe { TimeUs = rel }; c.Keyframes.Add(kf); }
        kf.Scale = a.D("scale") ?? c.Scale;
        kf.X = a.D("x") ?? c.X;
        kf.Y = a.D("y") ?? c.Y;
        kf.Rotation = a.D("rotation") ?? c.Rotation;
        kf.Opacity = a.D("opacity") ?? c.Opacity;
        c.Keyframes.Sort((x, y) => x.TimeUs.CompareTo(y.TimeUs));
        return Describe(c);
    }

    JsonNode ImportSrt(Args a)
    {
        var raw = a.S("srt_text") is { Length: > 0 } st ? st
            : a.S("path") is { Length: > 0 } p ? File.ReadAllText(Paths.Expand(p))
            : throw new EditException("Provide 'srt_text' or 'path'");
        var cues = Srt.Parse(raw);
        if (cues.Count == 0) throw new EditException("No valid SRT subtitle cues found");
        var res = new JsonArray();
        var tr = PickTrack("text", cues[0].StartUs, cues[^1].StartUs + cues[^1].DurUs - cues[0].StartUs);
        foreach (var (sUs, dUs, txt) in cues)
        {
            var c = new TClip
            {
                Kind = "text", Text = txt, Start = sUs, Dur = dUs,
                FontSize = a.D("font_size", 9), Color = Colors.Hex(a.S("color", "#FFFFFF")),
                Y = a.D("y", -0.75), StrokeColor = Colors.Hex(a.S("stroke_color", "#000000")),
            };
            tr.Clips.Add(c);
            res.Add(Describe(c));
        }
        Sort(tr);
        return res;
    }

    JsonNode Move(string id, double? start, string? toTrack)
    {
        var (t, c, _) = Find(id);
        if (start is { } s) c.Start = T.Us(s);
        if (!string.IsNullOrEmpty(toTrack))
        {
            var p = toTrack.Split('#');
            if (p[0] != t.Type) throw new EditException($"Can't move a {t.Type} segment to a {p[0]} track");
            TTrack dest;
            if (p.Length > 1 && p[1] == "new") dest = AddTrack(p[0]);
            else
            {
                var same = TracksOf(p[0]);
                int i = p.Length > 1 && int.TryParse(p[1], out var n) ? n : 0;
                dest = i >= same.Count ? AddTrack(p[0]) : same[i];
            }
            if (dest != t) { t.Clips.Remove(c); dest.Clips.Add(c); t = dest; }
        }
        Sort(t);
        return Describe(c);
    }

    int Delete(List<string> ids, bool ripple)
    {
        int n = 0;
        foreach (var id in ids)
        {
            var (t, c, _) = Find(id);
            t.Clips.Remove(c);
            n++;
            if (ripple && IsMain(t))
                foreach (var o in t.Clips.Where(o => o.Start >= c.Start)) o.Start -= c.Dur;
        }
        var main = TracksOf("video").FirstOrDefault();
        Doc.Tracks.RemoveAll(t => t.Clips.Count == 0 && t != main);
        return n;
    }

    JsonNode Split(string id, double at)
    {
        var (t, c, idx) = Find(id);
        long cut = T.Us(at) - c.Start;
        if (cut <= 0 || cut >= c.Dur) throw new EditException("Split point must be inside the segment");
        var b = JsonSerializer.Deserialize<TClip>(JsonSerializer.Serialize(c))!;
        b.Id = Ids.Short();
        b.Start = c.Start + cut;
        b.Dur = c.Dur - cut;
        if (c.Kind is "video" or "audio") b.In = c.In + (long)(cut * c.Speed);
        c.Dur = cut;
        c.Transition = null;
        c.VideoFadeOut = 0; c.AudioFadeOut = 0;
        b.VideoFadeIn = 0; b.AudioFadeIn = 0;
        b.Extra = [];
        t.Clips.Insert(idx + 1, b);
        return new JsonArray(Describe(c), Describe(b));
    }

    JsonNode Reorder(List<string> order)
    {
        var t = MainTrack();
        var picked = new List<TClip>();
        foreach (var id in order)
        {
            var (tr, c, _) = Find(id);
            if (tr != t) throw new EditException($"{id} is not on the main track");
            if (!picked.Contains(c)) picked.Add(c);
        }
        t.Clips = picked.Concat(t.Clips.Where(c => !picked.Contains(c))).ToList();
        long cur = 0;
        foreach (var c in t.Clips) { c.Start = cur; cur += c.Dur; }
        return new JsonArray(t.Clips.Select(c => (JsonNode)Describe(c)).ToArray());
    }

    int CloseGaps(string label)
    {
        var t = ByLabel(label);
        Sort(t);
        long cur = 0;
        int moved = 0;
        foreach (var c in t.Clips)
        {
            if (c.Start != cur) { c.Start = cur; moved++; }
            cur += c.Dur;
        }
        return moved;
    }

    JsonNode SetCanvas(string ratio)
    {
        (int w, int h) = ratio switch
        {
            "16:9" => (1920, 1080), "9:16" => (1080, 1920), "1:1" => (1080, 1080), "4:3" => (1440, 1080), "3:4" => (1080, 1440),
            "21:9" => (2560, 1080), "4:5" => (1080, 1350), "2:1" => (2160, 1080),
            _ => throw new EditException("Ratio must be one of 16:9, 9:16, 1:1, 4:3, 3:4, 21:9, 4:5, 2:1"),
        };
        Doc.Width = w; Doc.Height = h;
        return $"{w}x{h}";
    }

    JsonNode SetTransitions(Args a)
    {
        var ids = IdsOrAllMain(a);
        if (a.B("all_main", false) && ids.Count > 0) ids.RemoveAt(ids.Count - 1);
        var name = a.S("name");
        var res = new JsonArray();
        foreach (var id in ids)
        {
            var (t, c, idx) = Find(id);
            if (string.IsNullOrEmpty(name) || name == "none") { c.Transition = null; res.Add("removed"); continue; }
            long dur = T.Us(a.D("duration", 1.0));
            dur = Math.Min(dur, c.Dur / 2);
            if (idx + 1 < t.Clips.Count) dur = Math.Min(dur, t.Clips[idx + 1].Dur / 2);
            c.Transition = GenericCatalog.Normalize(name);
            c.TransitionDur = dur;
            res.Add($"{c.Transition} {T.Sec(dur)}s");
        }
        return res;
    }

    JsonNode SetAnimations(Args a)
    {
        var kind = a.S("kind", "in");
        var name = a.S("name");
        var res = new JsonArray();
        foreach (var id in IdsOrAllMain(a))
        {
            var c = Find(id).clip;
            long dur = Math.Min(T.Us(a.D("duration", 0.5)), c.Dur / 2);
            bool remove = string.IsNullOrEmpty(name) || name == "none";
            long v = remove ? 0 : dur;
            if (kind is "in" or "group" or "loop") c.VideoFadeIn = v;
            if (kind is "out" or "group" or "loop") c.VideoFadeOut = v;
            res.Add(remove ? "removed" : $"{kind}: fade {T.Sec(dur)}s");
        }
        return res;
    }

    JsonNode AddMotion(Args a)
    {
        var ids = IdsOrAllMain(a, true);
        var preset = a.S("preset", "zoom_in");
        string[] cycle = ["zoom_in", "pan_left", "zoom_out", "pan_right"];
        var res = new JsonArray();
        for (int i = 0; i < ids.Count; i++)
        {
            var c = Find(ids[i]).clip;
            var p = preset == "alternate" ? cycle[i % 4] : preset;
            c.Motion = p == "none" ? null : p;
            c.MotionStrength = a.D("strength", 0.15);
            res.Add($"{p} on {c.Id}");
        }
        return res;
    }
}
