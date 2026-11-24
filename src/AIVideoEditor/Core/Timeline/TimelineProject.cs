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
        "reorder_main_track", "close_gaps", "set_track_mute", "set_canvas", "set_transitions", "set_animations",
        "add_motion", "set_audio_fade", "search_catalog",
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
        foreach (var t in Doc.Tracks)
        {
            var i = counters.GetValueOrDefault(t.Type);
            counters[t.Type] = i + 1;
            tracks.Add(new JsonObject
            {
                ["track"] = $"{t.Type}#{i}" + (t.Type == "video" && i == 0 ? " (main)" : ""), ["muted"] = t.Muted,
                ["segments"] = new JsonArray(t.Clips.Select(c => (JsonNode)Describe(c)).ToArray()),
            });
        }
        return new JsonObject
        {
            ["editor"] = editor.Display, ["project"] = Name, ["duration_s"] = T.Sec(Doc.Duration),
            ["canvas"] = $"{Doc.Width}x{Doc.Height}", ["fps"] = Doc.Fps, ["tracks"] = tracks,
        };
    }

    static JsonObject Describe(TClip c)
    {
        var o = new JsonObject
        {
            ["id"] = c.Id[..Math.Min(8, c.Id.Length)], ["start"] = T.Sec(c.Start), ["end"] = T.Sec(c.End), ["dur"] = T.Sec(c.Dur),
            ["kind"] = c.Kind,
        };
        if (c.Kind == "text") o["text"] = c.Text;
        else if (c.FileName != "") o["file"] = c.FileName;
        if (c.X != 0 || c.Y != 0) o["pos"] = new JsonArray(Math.Round(c.X, 3), Math.Round(c.Y, 3));
        if (c.Scale != 1) o["scale"] = Math.Round(c.Scale, 3);
        if (c.Rotation != 0) o["rot"] = c.Rotation;
        if (c.Opacity != 1) o["opacity"] = c.Opacity;
        if (c.Volume != 1 && c.Kind is "video" or "audio") o["volume"] = c.Volume;
        if (c.Speed != 1) o["speed"] = c.Speed;
        var fx = new JsonArray();
        if (c.Transition != null) fx.Add($"transition:{c.Transition}");
        if (c.VideoFadeIn > 0) fx.Add("anim-in:Fade In");
        if (c.VideoFadeOut > 0) fx.Add("anim-out:Fade Out");
        if (c.AudioFadeIn > 0 || c.AudioFadeOut > 0) fx.Add($"fade:{T.Sec(c.AudioFadeIn)}/{T.Sec(c.AudioFadeOut)}");
        if (c.Motion != null) fx.Add($"motion:{c.Motion}");
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
        if (ch.S("text") is { } txt) c.Text = txt;
        if (ch.D("font_size") is { } fs) c.FontSize = fs;
        if (ch.S("color") is { } col) c.Color = Colors.Hex(col);
        if (ch.B("bold") is { } bo) c.Bold = bo;
        if (IsMain(t) && c.End != oldEnd && ch.D("start") is null)
            foreach (var o in t.Clips.Where(o => o != c && o.Start >= oldEnd)) o.Start += c.End - oldEnd;
        Sort(t);
        return Describe(c);
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
