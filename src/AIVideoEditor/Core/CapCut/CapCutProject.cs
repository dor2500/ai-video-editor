using System.Text.Json.Nodes;

namespace AIVideoEditor.Core.CapCut;

/// <summary>
/// Edits a CapCut desktop draft (draft_content.json) in place. Untouched fields are preserved as-is.
/// Times inside the draft are microseconds; tool arguments are seconds.
/// </summary>
public sealed class CapCutProject : EditorProject
{
    static readonly Lazy<CapCutCatalog> catalog = new(() => new CapCutCatalog());
    public static CapCutCatalog Catalog => catalog.Value;

    static readonly Dictionary<string, int> RenderBase = new()
    {
        ["video"] = 0, ["audio"] = 0, ["effect"] = 10000, ["filter"] = 11000, ["sticker"] = 14000, ["text"] = 15000,
    };

    public static readonly IReadOnlySet<string> Tools = new HashSet<string>
    {
        "add_media", "add_audio", "add_text", "edit_segments", "move_segment", "delete_segments", "split_segment",
        "reorder_main_track", "close_gaps", "set_track_mute", "set_track_hidden", "set_canvas", "set_transitions", "set_animations",
        "add_motion", "add_filter_or_effect", "set_audio_fade", "search_catalog",
    };

    readonly string folder;
    JsonObject d = null!;

    public CapCutProject(string folder)
    {
        this.folder = folder;
        if (!File.Exists(MainFile)) throw new EditException($"Not a CapCut project folder: {folder}");
        Reload();
    }

    public override string EditorId => "capcut";
    public override string MainFile => Path.Combine(folder, "draft_content.json");
    public string Folder => folder;
    public override IReadOnlySet<string> SupportedTools => Tools;
    public override string Notes =>
        "CapCut: effects/transitions/animations/filters/fonts come from search_catalog (names from the user's CapCut). " +
        "CapCut must be closed while editing; the user reopens the project in CapCut to see the result.";

    public override string Name
    {
        get
        {
            try { var n = Json.Load(Path.Combine(folder, "draft_meta_info.json")).Str("draft_name"); if (n != "") return n; } catch { }
            return Path.GetFileName(folder);
        }
    }

    public override void Reload() => d = Json.Load(MainFile)!.AsObject();

    JsonObject M => d["materials"]!.AsObject();
    JsonArray Tracks => d.Arr("tracks");
    JsonArray MList(string key) => M.Arr(key);

    public override void Save()
    {
        RecomputeDuration();
        var text = d.ToJsonString(Json.Relaxed);
        Json.SaveAtomic(MainFile, text);
        var tl = Path.Combine(folder, "Timelines", d.Str("id"), "draft_content.json");
        if (File.Exists(tl)) Json.SaveAtomic(tl, text);
        var dur = d.Long("duration");
        try
        {
            var metaPath = Path.Combine(folder, "draft_meta_info.json");
            var meta = Json.Load(metaPath)!;
            meta["tm_duration"] = dur;
            meta["tm_draft_modified"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            Json.SaveAtomic(metaPath, meta.ToJsonString(Json.Relaxed));
        }
        catch { }
        CapCutLibrary.UpdateRootMeta(folder, dur);
    }

    // ------------------------------------------------------------------ helpers
    void RecomputeDuration()
    {
        long end = 0;
        foreach (var t in Tracks)
            foreach (var s in t!["segments"]!.AsArray())
                end = Math.Max(end, s!["target_timerange"].Long("start") + s["target_timerange"].Long("duration"));
        d["duration"] = end;
    }

    (string? key, JsonObject? mat) FindMaterial(string id)
    {
        foreach (var kv in M)
            if (kv.Value is JsonArray a)
                foreach (var it in a)
                    if (it is JsonObject o && o.Str("id") == id) return (kv.Key, o);
        return (null, null);
    }

    (JsonObject track, JsonObject seg, int index) FindSegment(string sid)
    {
        foreach (var t in Tracks)
        {
            var segs = t!["segments"]!.AsArray();
            for (int i = 0; i < segs.Count; i++)
            {
                var id = segs[i].Str("id");
                if (id == sid || id.StartsWith(sid, StringComparison.OrdinalIgnoreCase))
                    return (t.AsObject(), segs[i]!.AsObject(), i);
            }
        }
        throw new EditException($"Segment '{sid}' not found. Call get_project_overview for ids.");
    }

    List<JsonObject> TracksOf(string type) => Tracks.Where(t => t.Str("type") == type).Select(t => t!.AsObject()).ToList();

    JsonObject AddTrack(string type)
    {
        var t = new JsonObject
        {
            ["id"] = Ids.CapCut(), ["type"] = type, ["segments"] = new JsonArray(), ["flag"] = 0, ["attribute"] = 0,
            ["name"] = "", ["is_default_name"] = true,
        };
        Tracks.Add(t);
        return t;
    }

    int RenderIndex(JsonObject track) =>
        RenderBase.GetValueOrDefault(track.Str("type")) + TracksOf(track.Str("type")).IndexOf(track);

    JsonObject? MainTrack(bool create = true)
    {
        var v = TracksOf("video");
        return v.Count > 0 ? v[0] : create ? AddTrack("video") : null;
    }

    static IEnumerable<JsonObject> Segs(JsonObject track) => track["segments"]!.AsArray().Select(s => s!.AsObject());
    static long Start(JsonObject s) => s["target_timerange"].Long("start");
    static long Dur(JsonObject s) => s["target_timerange"].Long("duration");
    static void SetStart(JsonObject s, long v) => s["target_timerange"]!["start"] = v;
    static long TrackEnd(JsonObject t) => Segs(t).Select(s => Start(s) + Dur(s)).DefaultIfEmpty(0).Max();

    static bool FreeAt(JsonObject t, long start, long dur) =>
        Segs(t).All(s => !(start < Start(s) + Dur(s) && Start(s) < start + dur));

    JsonObject PickTrack(string type, long start, long dur, bool newTrack = false)
    {
        if (!newTrack)
            foreach (var t in TracksOf(type).Skip(type == "video" ? 1 : 0))
                if (FreeAt(t, start, dur)) return t;
        return AddTrack(type);
    }

    JsonObject TrackByLabel(string label)
    {
        var parts = label.Split('#');
        var same = TracksOf(parts[0]);
        int i = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        if (i >= same.Count) throw new EditException($"No track {label}");
        return same[i];
    }

    static void SortTrack(JsonObject t)
    {
        var arr = t["segments"]!.AsArray();
        var sorted = arr.OrderBy(s => Start(s!.AsObject())).ToList(); // stable
        arr.Clear();
        foreach (var s in sorted) arr.Add(s);
    }

    static JsonObject Clip(double x = 0, double y = 0, double scale = 1) => new()
    {
        ["scale"] = new JsonObject { ["x"] = scale, ["y"] = scale }, ["rotation"] = 0.0, ["rotation3d"] = new JsonArray(),
        ["transform"] = new JsonObject { ["x"] = x, ["y"] = y },
        ["flip"] = new JsonObject { ["vertical"] = false, ["horizontal"] = false }, ["alpha"] = 1.0,
    };

    static JsonObject BaseSegment(string materialId, long start, long dur, long? srcStart, List<string> refs, bool visual,
        int renderIndex, double volume = 1.0) => new()
    {
        ["id"] = Ids.CapCut(),
        ["source_timerange"] = srcStart is { } ss ? new JsonObject { ["start"] = ss, ["duration"] = dur } : null,
        ["target_timerange"] = new JsonObject { ["start"] = start, ["duration"] = dur },
        ["render_timerange"] = new JsonObject { ["start"] = 0, ["duration"] = 0 },
        ["desc"] = "", ["state"] = 0, ["speed"] = 1.0, ["is_loop"] = false, ["is_tone_modify"] = false, ["reverse"] = false,
        ["intensifies_audio"] = false, ["cartoon"] = false, ["volume"] = volume, ["last_nonzero_volume"] = 1.0,
        ["clip"] = visual ? Clip() : null,
        ["uniform_scale"] = visual ? new JsonObject { ["on"] = true, ["value"] = 1.0 } : null,
        ["material_id"] = materialId,
        ["extra_material_refs"] = new JsonArray(refs.Select(r => (JsonNode)r).ToArray()),
        ["render_index"] = renderIndex, ["keyframe_refs"] = new JsonArray(),
        ["enable_lut"] = visual, ["enable_adjust"] = visual, ["enable_hsl"] = false, ["visible"] = true, ["group_id"] = "",
        ["enable_color_curves"] = true, ["enable_hsl_curves"] = true, ["track_render_index"] = renderIndex,
        ["hdr_settings"] = visual ? new JsonObject { ["mode"] = 1, ["intensity"] = 1.0, ["nits"] = 1000 } : null,
        ["enable_color_wheels"] = true, ["track_attribute"] = 0, ["is_placeholder"] = false, ["template_id"] = "",
        ["enable_smart_color_adjust"] = false, ["template_scene"] = "default", ["common_keyframes"] = new JsonArray(),
        ["caption_info"] = null,
        ["responsive_layout"] = new JsonObject
        {
            ["enable"] = false, ["target_follow"] = "", ["size_layout"] = 0, ["horizontal_pos_layout"] = 0,
            ["vertical_pos_layout"] = 0,
        },
        ["enable_color_match_adjust"] = false, ["enable_color_correct_adjust"] = false, ["enable_adjust_mask"] = false,
        ["raw_segment_id"] = "", ["lyric_keyframes"] = null, ["enable_video_mask"] = true,
        ["digital_human_template_group_id"] = "", ["color_correct_alg_result"] = "", ["source"] = "segmentsourcenormal",
        ["enable_mask_stroke"] = false, ["enable_mask_shadow"] = false, ["enable_color_adjust_pro"] = false,
        ["segment_color_tag"] = "", ["lumi_occlusion"] = null,
    };

    string NewMaterial(string list, JsonObject o)
    {
        o["id"] ??= Ids.CapCut();
        MList(list).Add(o);
        return o.Str("id");
    }

    string Speed() => NewMaterial("speeds", new JsonObject { ["type"] = "speed", ["mode"] = 0, ["speed"] = 1.0, ["curve_speed"] = null });
    string Canvas() => NewMaterial("canvases", new JsonObject
    {
        ["type"] = "canvas_color", ["color"] = "", ["blur"] = 0.0, ["image"] = "", ["album_image"] = "", ["image_id"] = "",
        ["image_name"] = "", ["source_platform"] = 0, ["team_id"] = "",
    });
    string SoundMap() => NewMaterial("sound_channel_mappings", new JsonObject
        { ["type"] = "none", ["audio_channel_mapping"] = 0, ["is_config_open"] = false });
    string VocalSep() => NewMaterial("vocal_separations", new JsonObject
    {
        ["type"] = "vocal_separation", ["choice"] = 0, ["removed_sounds"] = new JsonArray(), ["time_range"] = null,
        ["production_path"] = "", ["final_algorithm"] = "", ["enter_from"] = "",
    });

    // ------------------------------------------------------------------ overview
    public override JsonNode Overview()
    {
        RecomputeDuration();
        var c = d["canvas_config"];
        var tracks = new JsonArray();
        var counters = new Dictionary<string, int>();
        var usedMaterialIds = new HashSet<string>();
        foreach (var t in Tracks)
        {
            var type = t.Str("type");
            var idx = counters.GetValueOrDefault(type);
            counters[type] = idx + 1;
            bool isMain = type == "video" && idx == 0;
            var segs = new JsonArray();
            foreach (var s in Segs(t!.AsObject()))
            {
                var mid = s.Str("material_id");
                if (mid != "") usedMaterialIds.Add(mid);
                segs.Add(Describe(s));
            }
            var attr = t.Long("attribute");
            tracks.Add(new JsonObject
            {
                ["track"] = $"{type}#{idx}" + (isMain ? " (main)" : ""),
                ["type"] = type, ["index"] = idx, ["is_main"] = isMain,
                ["muted"] = (attr & 1) == 1, ["hidden"] = (attr & 2) == 2,
                ["segments"] = segs,
            });
        }
        var pool = new JsonArray();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in MList("videos"))
        {
            var raw = v.Str("path").Replace('/', '\\');
            if (raw == "" || !seenPaths.Add(raw)) continue;
            pool.Add(new JsonObject
            {
                ["path"] = raw, ["name"] = Path.GetFileName(raw) is { Length: > 0 } fn ? fn : v.Str("material_name"),
                ["kind"] = v.Str("type") == "photo" ? "photo" : "video",
                ["duration_s"] = T.Sec(v.Long("duration")),
                ["width"] = (int)v.Long("width"), ["height"] = (int)v.Long("height"),
                ["in_timeline"] = usedMaterialIds.Contains(v.Str("id")),
            });
        }
        foreach (var au in MList("audios"))
        {
            var raw = au.Str("path").Replace('/', '\\');
            if (raw == "" || !seenPaths.Add(raw)) continue;
            pool.Add(new JsonObject
            {
                ["path"] = raw, ["name"] = Path.GetFileName(raw) is { Length: > 0 } fn ? fn : au.Str("name"),
                ["kind"] = "audio",
                ["duration_s"] = T.Sec(au.Long("duration")),
                ["width"] = 0, ["height"] = 0,
                ["in_timeline"] = usedMaterialIds.Contains(au.Str("id")),
            });
        }
        var w = (int)c.Long("width", 1920);
        var h = (int)c.Long("height", 1080);
        var ratio = c.Str("ratio") is { Length: > 0 } r ? r : "16:9";
        return new JsonObject
        {
            ["editor"] = "CapCut", ["project"] = Name, ["duration_s"] = T.Sec(d.Long("duration")),
            ["canvas"] = $"{w}x{h} ({ratio})", ["width"] = w, ["height"] = h, ["ratio"] = ratio,
            ["fps"] = d.Dbl("fps", 30), ["can_undo"] = CanUndo, ["can_redo"] = CanRedo,
            ["media_pool"] = pool, ["tracks"] = tracks,
        };
    }

    JsonObject Describe(JsonObject s)
    {
        var info = new JsonObject
        {
            ["id"] = s.Str("id")[..8], ["start"] = T.Sec(Start(s)), ["end"] = T.Sec(Start(s) + Dur(s)), ["dur"] = T.Sec(Dur(s)),
            ["source_in"] = T.Sec(s["source_timerange"].Long("start")),
            ["visible"] = s.Bool("visible", true),
            ["flip_h"] = s["clip"]?["flip"]?.Bool("horizontal", false) ?? false,
            ["flip_v"] = s["clip"]?["flip"]?.Bool("vertical", false) ?? false,
        };
        var (key, mat) = FindMaterial(s.Str("material_id"));
        if (mat != null)
        {
            switch (key)
            {
                case "videos":
                    info["kind"] = mat.Str("type");
                    info["path"] = mat.Str("path").Replace('/', '\\');
                    info["source_dur"] = T.Sec(mat.Long("duration"));
                    info["file"] = Path.GetFileName(mat.Str("path")) is { Length: > 0 } fn ? fn : mat.Str("material_name");
                    break;
                case "audios":
                    info["kind"] = "audio";
                    info["path"] = mat.Str("path").Replace('/', '\\');
                    info["source_dur"] = T.Sec(mat.Long("duration"));
                    info["file"] = mat.Str("name");
                    break;
                case "texts":
                    info["kind"] = "text";
                    try
                    {
                        var content = JsonNode.Parse(mat.Str("content", "{}"));
                        var t = content.Str("text");
                        info["text"] = t.Length > 80 ? t[..80] : t;
                        info["align"] = mat.Long("alignment", 1) switch { 0 => "left", 2 => "right", _ => "center" };
                        var st0 = content?["styles"]?.AsArray().FirstOrDefault();
                        if (st0 != null)
                        {
                            info["font_size"] = st0.Dbl("size", 8);
                            info["bold"] = st0.Bool("bold", false);
                            if (st0["fill"]?["content"]?["solid"]?["color"] is JsonArray rgb && rgb.Count >= 3)
                            {
                                int r = (int)Math.Round(Math.Clamp(rgb[0]!.GetValue<double>(), 0, 1) * 255);
                                int g = (int)Math.Round(Math.Clamp(rgb[1]!.GetValue<double>(), 0, 1) * 255);
                                int b = (int)Math.Round(Math.Clamp(rgb[2]!.GetValue<double>(), 0, 1) * 255);
                                info["color"] = $"#{r:X2}{g:X2}{b:X2}";
                            }
                        }
                    }
                    catch { info["text"] = "?"; }
                    break;
                default:
                    info["kind"] = mat.Str("type") is { Length: > 0 } ty ? ty : key!.TrimEnd('s');
                    if (mat.Str("name") is { Length: > 0 } nm) info["name"] = nm;
                    break;
            }
        }
        if (s["clip"] is JsonObject clip)
        {
            double x = clip["transform"].Dbl("x"), y = clip["transform"].Dbl("y");
            if (x != 0 || y != 0) info["pos"] = new JsonArray(Math.Round(x, 3), Math.Round(y, 3));
            var sc = clip["scale"].Dbl("x", 1);
            if (sc != 1) info["scale"] = Math.Round(sc, 3);
            if (clip.Dbl("rotation") != 0) info["rot"] = clip.Dbl("rotation");
            if (clip.Dbl("alpha", 1) != 1) info["opacity"] = clip.Dbl("alpha", 1);
        }
        if (s.Dbl("volume", 1) != 1 && info.Str("kind") is "video" or "audio") info["volume"] = s.Dbl("volume", 1);
        if (s.Dbl("speed", 1) != 1) info["speed"] = s.Dbl("speed", 1);
        var fx = new JsonArray();
        foreach (var r in s["extra_material_refs"]?.AsArray() ?? [])
        {
            var (k, em) = FindMaterial(r!.GetValue<string>());
            if (k == "transitions")
            {
                var tn = em.Str("name");
                fx.Add($"transition:{tn}");
                info["transition"] = tn;
                info["transition_dur"] = T.Sec(em.Long("duration"));
            }
            else if (k == "material_animations")
                foreach (var a in em!["animations"]?.AsArray() ?? []) fx.Add($"anim-{a.Str("type")}:{a.Str("name")}");
            else if (k == "audio_fades")
            {
                var fi = em.Long("fade_in_duration");
                var fo = em.Long("fade_out_duration");
                fx.Add($"fade:{T.Sec(fi)}/{T.Sec(fo)}");
                if (fi > 0) info["fade_in"] = T.Sec(fi);
                if (fo > 0) info["fade_out"] = T.Sec(fo);
            }
        }
        if (s["common_keyframes"] is JsonArray kfs && kfs.Count > 0) fx.Add("keyframes");
        if (fx.Count > 0) info["fx"] = fx;
        return info;
    }

    JsonObject DescribeId(string id) => Describe(FindSegment(id).seg);

    // ------------------------------------------------------------------ dispatch
    public override JsonNode? Run(string tool, Args a) => tool switch
    {
        "add_media" => AddMedia(a),
        "add_audio" => AddAudio(a),
        "add_text" => AddText(a),
        "edit_segments" => new JsonArray(a.L("ids").Select(id => (JsonNode)EditSegment(id, a)).ToArray()),
        "move_segment" => MoveSegment(a.Req("id"), a.D("start"), a.S("to_track")),
        "delete_segments" => Obj(new { deleted = DeleteSegments(a.L("ids"), a.B("ripple", true)) }),
        "split_segment" => SplitSegment(a.Req("id"), a.ReqD("at")),
        "reorder_main_track" => ReorderMain(a.L("order")),
        "close_gaps" => Obj(new { moved = CloseGaps(a.S("track", "video#0")) }),
        "set_track_mute" => SetMute(a.Req("track"), a.B("muted", true)),
        "set_track_hidden" => SetHidden(a.Req("track"), a.B("hidden", true)),
        "set_canvas" => SetCanvas(a.Req("ratio")),
        "search_catalog" => Catalog.Search(a.Req("category"), a.S("query", ""), (int)a.D("limit", 60)),
        "set_transitions" => SetTransitions(a),
        "set_animations" => SetAnimations(a),
        "add_motion" => AddMotion(a),
        "add_filter_or_effect" => AddTrackEffect(a.Req("category"), Catalog.Get(a.Req("category"), a.Req("name")),
            a.ReqD("start"), a.ReqD("duration"), a.D("intensity", 1.0)),
        "set_audio_fade" => new JsonArray(a.L("ids").Select(id => (JsonNode)SetAudioFade(id, a.D("fade_in", 0), a.D("fade_out", 0))).ToArray()),
        _ => throw new EditException($"'{tool}' is not supported for CapCut projects"),
    };

    List<string> IdsOrAllMain(Args a, bool photosOnly = false)
    {
        var ids = a.L("ids");
        if (a.B("all_main", false) || a.B("all_main_photos", false))
        {
            var t = MainTrack(false);
            if (t != null)
                ids.AddRange(Segs(t).Where(s => !photosOnly || FindMaterial(s.Str("material_id")).mat.Str("type") == "photo")
                    .Select(s => s.Str("id")));
        }
        if (ids.Count == 0) throw new EditException("No segment ids given");
        return ids;
    }

    // ------------------------------------------------------------------ media
    JsonObject VideoMaterial(string path, MediaInfo pr)
    {
        var norm = path.Replace('\\', '/');
        foreach (var v in MList("videos"))
            if (string.Equals(v.Str("path"), norm, StringComparison.OrdinalIgnoreCase)) return v!.AsObject();
        var mat = new JsonObject
        {
            ["id"] = Ids.CapCut(), ["unique_id"] = "", ["type"] = pr.Kind == MediaKind.Photo ? "photo" : "video",
            ["duration"] = pr.DurationUs, ["path"] = norm, ["media_path"] = "", ["local_id"] = "", ["has_audio"] = pr.HasAudio,
            ["width"] = pr.Width, ["height"] = pr.Height, ["category_id"] = "", ["category_name"] = "local",
            ["material_id"] = "", ["material_name"] = Path.GetFileName(path), ["material_url"] = "",
            ["crop"] = new JsonObject
            {
                ["upper_left_x"] = 0.0, ["upper_left_y"] = 0.0, ["upper_right_x"] = 1.0, ["upper_right_y"] = 0.0,
                ["lower_left_x"] = 0.0, ["lower_left_y"] = 1.0, ["lower_right_x"] = 1.0, ["lower_right_y"] = 1.0,
            },
            ["crop_ratio"] = "free", ["audio_fade"] = null, ["crop_scale"] = 1.0, ["extra_type_option"] = 0, ["source"] = 0,
            ["source_platform"] = 0, ["formula_id"] = "", ["check_flag"] = 62978047,
            ["local_material_id"] = Guid.NewGuid().ToString(), ["origin_material_id"] = "", ["request_id"] = "",
            ["has_sound_separated"] = false, ["picture_from"] = "none",
        };
        MList("videos").Add(mat);
        return mat;
    }

    JsonObject AudioMaterial(string path, MediaInfo pr)
    {
        var norm = path.Replace('\\', '/');
        foreach (var v in MList("audios"))
            if (string.Equals(v.Str("path"), norm, StringComparison.OrdinalIgnoreCase)) return v!.AsObject();
        var mat = new JsonObject
        {
            ["app_id"] = 0, ["category_id"] = "", ["category_name"] = "local", ["check_flag"] = 3,
            ["copyright_limit_type"] = "none", ["duration"] = pr.DurationUs, ["effect_id"] = "", ["formula_id"] = "",
            ["id"] = Ids.CapCut(), ["local_material_id"] = Guid.NewGuid().ToString(), ["music_id"] = Guid.NewGuid().ToString(),
            ["name"] = Path.GetFileName(path), ["path"] = norm, ["source_platform"] = 0, ["type"] = "extract_music",
            ["wave_points"] = new JsonArray(),
        };
        MList("audios").Add(mat);
        return mat;
    }

    JsonNode AddMedia(Args a)
    {
        var target = a.S("target", "main");
        var files = Media.ExpandPaths(a.L("paths"), true).ToList();
        if (a.B("sort_by_name", true)) files.Sort((x, y) => string.Compare(Path.GetFileName(x), Path.GetFileName(y), StringComparison.OrdinalIgnoreCase));
        if (files.Count == 0) throw new EditException("No images/videos found in the given paths");
        var added = new List<string>();
        JsonObject? main = target == "main" ? MainTrack() : null;
        long cursor = a.D("start") is { } st ? T.Us(st) : main != null ? TrackEnd(main) : 0;
        foreach (var f in files)
        {
            var pr = Media.Probe(f);
            if (pr.Kind == MediaKind.Audio) throw new EditException($"{f} is audio; use add_audio");
            var mat = VideoMaterial(Path.GetFullPath(f), pr);
            long dur = pr.Kind == MediaKind.Photo ? T.Us(a.D("photo_duration", 3)) : pr.DurationUs;
            if (pr.Kind == MediaKind.Video && a.D("max_video_duration") is { } mx) dur = Math.Min(dur, T.Us(mx));
            JsonObject tr;
            if (main != null)
            {
                tr = main;
                if (!FreeAt(tr, cursor, dur))
                    foreach (var s in Segs(tr))
                        if (Start(s) >= cursor) SetStart(s, Start(s) + dur);
            }
            else tr = PickTrack("video", cursor, dur, target == "new_track");
            var ri = RenderIndex(tr);
            var seg = BaseSegment(mat.Str("id"), cursor, dur, 0, [Speed(), Canvas(), SoundMap(), VocalSep()], true, ri);
            if (target == "overlay") seg["clip"]!["scale"] = new JsonObject { ["x"] = 0.5, ["y"] = 0.5 };
            tr["segments"]!.AsArray().Add(seg);
            added.Add(seg.Str("id"));
            if (main != null) cursor += dur;
            SortTrack(tr);
        }
        return new JsonArray(added.Select(id => (JsonNode)DescribeId(id)).ToArray());
    }

    JsonNode AddAudio(Args a)
    {
        var path = Paths.Expand(a.Req("path"));
        var pr = Media.Probe(path);
        if (pr.Kind == MediaKind.Photo) throw new EditException("That's an image, not audio");
        var mat = AudioMaterial(path, pr);
        long st = T.Us(a.D("start", 0)), src0 = T.Us(a.D("trim_start", 0));
        long avail = pr.DurationUs - src0;
        if (avail <= 0) throw new EditException("trim_start is beyond the end of the audio");
        bool fit = a.B("fit_to_video", false), loop = a.B("loop", false);
        long total;
        if (fit)
        {
            var vidEnd = TracksOf("video").Select(TrackEnd).DefaultIfEmpty(0).Max();
            total = vidEnd - st;
            if (total <= 0) throw new EditException("There is no video to fit the audio to");
        }
        else total = a.D("duration") is { } du ? T.Us(du) : avail;
        var pieces = new List<(long t, long d)>();
        if (loop || fit)
        {
            for (long rem = total, t = st; rem > 0;) { var dd = Math.Min(rem, avail); pieces.Add((t, dd)); t += dd; rem -= dd; }
        }
        else pieces.Add((st, Math.Min(total, avail)));
        var full = pieces[^1].t + pieces[^1].d - pieces[0].t;
        var tr = PickTrack("audio", pieces[0].t, full, a.B("new_track", false));
        var ids = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            var refs = new List<string> { Speed(), SoundMap(), VocalSep() };
            long fi = i == 0 ? T.Us(a.D("fade_in", 0)) : 0, fo = i == pieces.Count - 1 ? T.Us(a.D("fade_out", 0)) : 0;
            if (fi > 0 || fo > 0)
                refs.Add(NewMaterial("audio_fades", new JsonObject
                    { ["fade_in_duration"] = fi, ["fade_out_duration"] = fo, ["fade_type"] = 0, ["type"] = "audio_fade" }));
            var seg = BaseSegment(mat.Str("id"), pieces[i].t, pieces[i].d, src0, refs, false, 0, a.D("volume", 1.0));
            tr["segments"]!.AsArray().Add(seg);
            ids.Add(seg.Str("id"));
        }
        SortTrack(tr);
        return new JsonArray(ids.Select(id => (JsonNode)DescribeId(id)).ToArray());
    }

    JsonNode AddText(Args a)
    {
        var text = a.Req("text");
        var style = new JsonObject
        {
            ["fill"] = new JsonObject
            {
                ["alpha"] = 1.0,
                ["content"] = new JsonObject
                {
                    ["render_type"] = "solid",
                    ["solid"] = new JsonObject { ["alpha"] = 1.0, ["color"] = Json.From(Colors.Rgb(a.S("color", "#FFFFFF"))) },
                },
            },
            ["range"] = new JsonArray(0, text.Length), ["size"] = a.D("font_size", 8), ["bold"] = a.B("bold", false),
            ["italic"] = a.B("italic", false), ["underline"] = false, ["strokes"] = new JsonArray(),
        };
        int check = 7;
        if (a.S("stroke_color") is { Length: > 0 } sc)
        {
            style["strokes"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["solid"] = new JsonObject { ["alpha"] = 1.0, ["color"] = Json.From(Colors.Rgb(sc)) } },
                ["width"] = a.D("stroke_width", 0.08),
            });
            check |= 8;
        }
        if (a.B("shadow", false))
        {
            style["shadows"] = new JsonArray(new JsonObject
            {
                ["diffuse"] = 15 / 100.0 / 6, ["alpha"] = 0.8, ["distance"] = 5.0,
                ["content"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonArray(0, 0, 0) } }, ["angle"] = -45.0,
            });
            check |= 32;
        }
        if (a.S("font") is { Length: > 0 } font)
        {
            var f = Catalog.Get("font", font);
            style["font"] = new JsonObject { ["id"] = f.ResourceId, ["path"] = "D:" };
        }
        var content = new JsonObject { ["styles"] = new JsonArray(style), ["text"] = text };
        var mat = new JsonObject
        {
            ["content"] = content.ToJsonString(Json.Relaxed), ["typesetting"] = 0,
            ["alignment"] = a.S("align", "center") switch { "left" => 0, "right" => 2, _ => 1 },
            ["letter_spacing"] = 0.0, ["line_spacing"] = 0.02, ["line_feed"] = 1, ["line_max_width"] = 0.82,
            ["force_apply_line_max_width"] = false, ["check_flag"] = check, ["type"] = "text", ["global_alpha"] = 1.0,
        };
        if (a.S("background_color") is { Length: > 0 } bg)
        {
            mat["background_style"] = 1; mat["background_color"] = Colors.Hex(bg); mat["background_alpha"] = a.D("background_alpha", 0.6);
            mat["background_round_radius"] = 0.0; mat["background_height"] = 0.14; mat["background_width"] = 0.14;
            mat["background_horizontal_offset"] = 0.5; mat["background_vertical_offset"] = 0.5;
            mat["check_flag"] = check | 16;
        }
        var mid = NewMaterial("texts", mat);
        long st = T.Us(a.ReqD("start")), du = T.Us(a.ReqD("duration"));
        var tr = PickTrack("text", st, du);
        var seg = BaseSegment(mid, st, du, null, [], true, RenderIndex(tr));
        seg["clip"]!["transform"] = new JsonObject { ["x"] = a.D("x", 0), ["y"] = a.D("y", -0.7) };
        tr["segments"]!.AsArray().Add(seg);
        SortTrack(tr);
        return DescribeId(seg.Str("id"));
    }

    // ------------------------------------------------------------------ editing
    JsonObject EditSegment(string sid, Args ch)
    {
        var (track, s, _) = FindSegment(sid);
        var id = s.Str("id");
        long oldEnd = Start(s) + Dur(s);
        bool isMain = ReferenceEquals(track, MainTrack(false));
        var tr = s["target_timerange"]!;
        if (ch.D("start") is { } st) tr["start"] = T.Us(st);
        if (ch.D("duration") is { } dd)
        {
            long nd = T.Us(dd);
            var (key, mat) = FindMaterial(s.Str("material_id"));
            if (s["source_timerange"] is JsonObject src)
            {
                double spd = s.Dbl("speed", 1);
                if (key is "videos" or "audios" && mat.Str("type") != "photo")
                {
                    long max = (long)((mat.Long("duration") - src.Long("start")) / spd);
                    if (nd > max) throw new EditException($"The clip is only {T.Sec(max)}s long from its trim point");
                    src["duration"] = (long)(nd * spd);
                }
                else src["duration"] = nd;
            }
            tr["duration"] = nd;
        }
        if (s["clip"] is JsonObject clip)
        {
            if (ch.D("x") is { } x) clip["transform"]!["x"] = x;
            if (ch.D("y") is { } y) clip["transform"]!["y"] = y;
            if (ch.D("scale") is { } sc) clip["scale"] = new JsonObject { ["x"] = sc, ["y"] = sc };
            if (ch.D("rotation") is { } r) clip["rotation"] = r;
            if (ch.D("opacity") is { } op) clip["alpha"] = op;
            if (ch.B("flip_horizontal") is { } fh) clip["flip"]!["horizontal"] = fh;
            if (ch.B("flip_vertical") is { } fv) clip["flip"]!["vertical"] = fv;
        }
        if (ch.D("volume") is { } vol) { s["volume"] = vol; if (vol > 0) s["last_nonzero_volume"] = vol; }
        if (ch.D("speed") is { } spd2)
        {
            if (spd2 <= 0) throw new EditException("speed must be > 0");
            if (s["source_timerange"] is JsonObject src2) tr["duration"] = (long)(src2.Long("duration") / spd2);
            s["speed"] = spd2;
            foreach (var r in s["extra_material_refs"]!.AsArray())
                if (FindMaterial(r!.GetValue<string>()) is ("speeds", { } sm)) sm["speed"] = spd2;
        }
        if (ch.B("visible") is { } vis) s["visible"] = vis;
        var (k2, m2) = FindMaterial(s.Str("material_id"));
        if (k2 == "texts" && m2 != null)
        {
            var content = JsonNode.Parse(m2.Str("content", "{}"))!.AsObject();
            var styles = content.Arr("styles");
            if (ch.S("text") is { } txt)
            {
                content["text"] = txt;
                while (styles.Count > 1) styles.RemoveAt(styles.Count - 1);
                foreach (var sty in styles) sty!["range"] = new JsonArray(0, txt.Length);
            }
            foreach (var sty in styles)
            {
                if (ch.D("font_size") is { } fs) sty!["size"] = fs;
                if (ch.S("color") is { } col) sty!["fill"] = new JsonObject
                {
                    ["alpha"] = 1.0,
                    ["content"] = new JsonObject
                    {
                        ["render_type"] = "solid",
                        ["solid"] = new JsonObject { ["alpha"] = 1.0, ["color"] = Json.From(Colors.Rgb(col)) },
                    },
                };
                if (ch.B("bold") is { } b) sty!["bold"] = b;
            }
            m2["content"] = content.ToJsonString(Json.Relaxed);
            if (m2["words"] is JsonObject) m2["words"] = new JsonObject
                { ["start_time"] = new JsonArray(), ["end_time"] = new JsonArray(), ["text"] = new JsonArray() };
        }
        // the main track is magnetic in CapCut: keep later clips butted up against this one
        long newEnd = Start(s) + Dur(s);
        if (isMain && newEnd != oldEnd && ch.D("start") is null)
            foreach (var o in Segs(track))
                if (o.Str("id") != id && Start(o) >= oldEnd) SetStart(o, Start(o) + newEnd - oldEnd);
        SortTrack(track);
        return DescribeId(id);
    }

    JsonNode MoveSegment(string sid, double? start, string? toTrack)
    {
        var (track, s, _) = FindSegment(sid);
        var id = s.Str("id");
        if (start is { } st) SetStart(s, T.Us(st));
        if (!string.IsNullOrEmpty(toTrack))
        {
            var parts = toTrack.Split('#');
            if (parts[0] != track.Str("type")) throw new EditException($"Can't move a {track.Str("type")} segment to a {parts[0]} track");
            JsonObject dest;
            if (parts.Length > 1 && parts[1] == "new") dest = AddTrack(parts[0]);
            else
            {
                var same = TracksOf(parts[0]);
                int i = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
                dest = i >= same.Count ? AddTrack(parts[0]) : same[i];
            }
            if (!ReferenceEquals(dest, track))
            {
                track["segments"]!.AsArray().Remove(s);
                dest["segments"]!.AsArray().Add(s);
                var ri = RenderIndex(dest);
                s["render_index"] = ri;
                s["track_render_index"] = ri;
                track = dest;
            }
        }
        SortTrack(track);
        return DescribeId(id);
    }

    int DeleteSegments(List<string> ids, bool ripple)
    {
        int n = 0;
        foreach (var sid in ids)
        {
            var (track, s, _) = FindSegment(sid);
            long st = Start(s), du = Dur(s);
            bool isMain = ReferenceEquals(track, MainTrack(false));
            track["segments"]!.AsArray().Remove(s);
            n++;
            if (ripple && isMain)
                foreach (var o in Segs(track))
                    if (Start(o) >= st) SetStart(o, Start(o) - du);
        }
        var main = MainTrack(false);
        foreach (var t in Tracks.ToList())
            if (t!["segments"]!.AsArray().Count == 0 && !ReferenceEquals(t, main)) Tracks.Remove(t);
        return n;
    }

    static readonly HashSet<string> PerSegmentMaterials =
        ["speeds", "canvases", "sound_channel_mappings", "vocal_separations", "material_colors", "placeholder_infos",
         "material_animations", "audio_fades"];

    JsonNode SplitSegment(string sid, double at)
    {
        var (track, s, idx) = FindSegment(sid);
        long cut = T.Us(at) - Start(s), du = Dur(s);
        if (cut <= 0 || cut >= du) throw new EditException("Split point must be inside the segment");
        var b = s.DeepClone().AsObject();
        b["id"] = Ids.CapCut();
        var newRefs = new JsonArray();
        foreach (var r in b["extra_material_refs"]!.AsArray())
        {
            var rid = r!.GetValue<string>();
            var (k, em) = FindMaterial(rid);
            if (k != null && PerSegmentMaterials.Contains(k))
            {
                var cp = em!.DeepClone().AsObject();
                cp["id"] = Ids.CapCut();
                MList(k).Add(cp);
                newRefs.Add(cp.Str("id"));
            }
            else if (k == "transitions") continue; // transition stays at the end of the second half only
            else newRefs.Add(rid);
        }
        b["extra_material_refs"] = newRefs;
        var keep = new JsonArray(s["extra_material_refs"]!.AsArray()
            .Where(r => FindMaterial(r!.GetValue<string>()).key != "transitions").Select(r => r!.DeepClone()).ToArray());
        // first half keeps its transition-free refs; the second half gets the original transition
        var trans = s["extra_material_refs"]!.AsArray().Where(r => FindMaterial(r!.GetValue<string>()).key == "transitions")
            .Select(r => r!.GetValue<string>()).ToList();
        s["extra_material_refs"] = keep;
        foreach (var t in trans) newRefs.Add(t);
        double spd = s.Dbl("speed", 1);
        b["target_timerange"] = new JsonObject { ["start"] = Start(s) + cut, ["duration"] = du - cut };
        if (s["source_timerange"] is JsonObject src)
        {
            b["source_timerange"] = new JsonObject { ["start"] = src.Long("start") + (long)(cut * spd), ["duration"] = (long)((du - cut) * spd) };
            src["duration"] = (long)(cut * spd);
        }
        s["target_timerange"]!["duration"] = cut;
        b["common_keyframes"] = new JsonArray();
        track["segments"]!.AsArray().Insert(idx + 1, b);
        return new JsonArray(DescribeId(s.Str("id")), DescribeId(b.Str("id")));
    }

    int CloseGaps(string label)
    {
        var t = TrackByLabel(label);
        SortTrack(t);
        long cur = 0;
        int moved = 0;
        foreach (var s in Segs(t))
        {
            if (Start(s) != cur) { SetStart(s, cur); moved++; }
            cur += Dur(s);
        }
        return moved;
    }

    JsonNode ReorderMain(List<string> order)
    {
        var t = MainTrack(false) ?? throw new EditException("No main track");
        var picked = new List<JsonObject>();
        foreach (var sid in order)
        {
            var (tr, s, _) = FindSegment(sid);
            if (!ReferenceEquals(tr, t)) throw new EditException($"{sid} is not on the main track");
            if (!picked.Contains(s)) picked.Add(s);
        }
        var rest = Segs(t).Where(s => !picked.Contains(s)).ToList();
        var arr = t["segments"]!.AsArray();
        arr.Clear();
        foreach (var s in picked.Concat(rest)) arr.Add(s);
        long cur = 0;
        foreach (var s in Segs(t)) { SetStart(s, cur); cur += Dur(s); }
        return new JsonArray(Segs(t).Select(s => (JsonNode)Describe(s)).ToArray());
    }

    JsonNode SetMute(string label, bool muted)
    {
        var t = TrackByLabel(label);
        var attr = t.Long("attribute");
        t["attribute"] = muted ? attr | 1 : attr & ~1L;
        return "ok";
    }

    JsonNode SetHidden(string label, bool hidden)
    {
        var t = TrackByLabel(label);
        var attr = t.Long("attribute");
        t["attribute"] = hidden ? attr | 2 : attr & ~2L;
        return "ok";
    }

    JsonNode SetCanvas(string ratio)
    {
        var presets = new Dictionary<string, (int, int)>
        {
            ["16:9"] = (1920, 1080), ["9:16"] = (1080, 1920), ["1:1"] = (1080, 1080), ["4:3"] = (1440, 1080),
            ["3:4"] = (1080, 1440), ["21:9"] = (2560, 1080), ["4:5"] = (1080, 1350), ["2:1"] = (2160, 1080),
        };
        if (!presets.TryGetValue(ratio, out var wh)) throw new EditException($"Ratio must be one of {string.Join(", ", presets.Keys)}");
        var c = d["canvas_config"]!.AsObject();
        c["ratio"] = ratio; c["width"] = wh.Item1; c["height"] = wh.Item2;
        return $"{wh.Item1}x{wh.Item2}";
    }

    // ------------------------------------------------------------------ effects
    JsonNode SetTransitions(Args a)
    {
        var ids = IdsOrAllMain(a);
        if (a.B("all_main", false) && ids.Count > 0) ids.RemoveAt(ids.Count - 1);
        var name = a.S("name");
        var item = string.IsNullOrEmpty(name) || name == "none" ? null : Catalog.Get("transition", name);
        var res = new JsonArray();
        foreach (var sid in ids)
        {
            var (track, s, idx) = FindSegment(sid);
            var refs = s["extra_material_refs"]!.AsArray();
            foreach (var r in refs.ToList())
                if (FindMaterial(r!.GetValue<string>()).key == "transitions") refs.Remove(r);
            if (item == null) { res.Add("removed"); continue; }
            long dur = a.D("duration") is { } du ? T.Us(du) : item.DurationUs > 0 ? item.DurationUs : 500_000;
            var segs = track["segments"]!.AsArray();
            if (idx + 1 < segs.Count)
                dur = Math.Min(dur, Math.Min(Dur(s) / 2, Dur(segs[idx + 1]!.AsObject()) / 2));
            var id = NewMaterial("transitions", new JsonObject
            {
                ["type"] = "transition", ["name"] = item.Name, ["effect_id"] = item.EffectId, ["resource_id"] = item.ResourceId,
                ["third_resource_id"] = "", ["source_platform"] = 1, ["sub_type"] = 0, ["adjust_params"] = new JsonArray(),
                ["path"] = item.Path, ["duration"] = dur, ["is_overlap"] = item.IsOverlap, ["platform"] = "all",
                ["category_id"] = item.CategoryId, ["category_name"] = "", ["request_id"] = "", ["is_ai_transition"] = false,
                ["video_path"] = "", ["task_id"] = "",
            });
            refs.Add(id);
            res.Add($"{item.Name} {T.Sec(dur)}s");
        }
        return res;
    }

    JsonNode SetAnimations(Args a)
    {
        var res = new JsonArray();
        foreach (var sid in IdsOrAllMain(a))
        {
            var (track, s, _) = FindSegment(sid);
            bool isText = track.Str("type") == "text";
            var kind = a.S("kind", "in");
            if (isText && kind == "group") kind = "loop";
            if (!isText && kind == "loop") kind = "group";
            var cat = (kind, isText) switch
            {
                ("in", false) => "video_in", ("out", false) => "video_out", ("group", false) => "video_combo",
                ("in", true) => "text_in", ("out", true) => "text_out", _ => "text_loop",
            };
            var name = a.S("name");
            var item = string.IsNullOrEmpty(name) || name == "none" ? null : Catalog.Get(cat, name);
            JsonObject? anim = null;
            foreach (var r in s["extra_material_refs"]!.AsArray())
                if (FindMaterial(r!.GetValue<string>()) is ("material_animations", { } am)) anim = am;
            if (anim == null)
            {
                if (item == null) { res.Add("nothing to remove"); continue; }
                anim = new JsonObject
                {
                    ["id"] = Ids.CapCut(), ["type"] = isText ? "sticker_animation" : "video_animation",
                    ["animations"] = new JsonArray(), ["multi_language_current"] = "none",
                };
                MList("material_animations").Add(anim);
                s["extra_material_refs"]!.AsArray().Add(anim.Str("id"));
            }
            var drop = new HashSet<string> { kind };
            if (kind is "group" or "loop") { drop.Add("in"); drop.Add("out"); } else { drop.Add("group"); drop.Add("loop"); }
            var list = anim["animations"]!.AsArray();
            foreach (var x in list.ToList()) if (drop.Contains(x.Str("type"))) list.Remove(x);
            if (item == null) { res.Add("removed"); continue; }
            long segD = Dur(s), dur, start;
            if (kind is "group" or "loop") { dur = a.D("duration") is { } g ? T.Us(g) : segD; start = 0; }
            else
            {
                dur = Math.Min(a.D("duration") is { } g ? T.Us(g) : item.DurationUs, segD);
                start = kind == "in" ? 0 : segD - dur;
            }
            list.Add(new JsonObject
            {
                ["id"] = item.EffectId, ["type"] = kind, ["start"] = start, ["duration"] = dur, ["path"] = item.Path,
                ["platform"] = "all", ["resource_id"] = item.ResourceId, ["third_resource_id"] = "", ["source_platform"] = 1,
                ["name"] = item.Name, ["category_id"] = item.CategoryId, ["category_name"] = "",
                ["panel"] = isText ? "" : "video", ["material_type"] = isText ? "sticker" : "video",
                ["anim_adjust_params"] = null, ["request_id"] = "",
            });
            res.Add($"{kind}: {item.Name} {T.Sec(dur)}s");
        }
        return res;
    }

    JsonNode AddTrackEffect(string category, CatalogItem item, double start, double duration, double intensity)
    {
        long st = T.Us(start), du = T.Us(duration);
        string id, ttype;
        if (category == "filter")
        {
            id = NewMaterial("effects", new JsonObject
            {
                ["adjust_params"] = new JsonArray(), ["algorithm_artifact_path"] = "", ["apply_target_type"] = 0,
                ["bloom_params"] = null, ["category_id"] = "", ["category_name"] = "",
                ["color_match_info"] = new JsonObject { ["source_feature_path"] = "", ["target_feature_path"] = "", ["target_image_path"] = "" },
                ["effect_id"] = item.EffectId, ["enable_skin_tone_correction"] = false, ["exclusion_group"] = new JsonArray(),
                ["face_adjust_params"] = new JsonArray(), ["formula_id"] = "", ["intensity_key"] = "",
                ["multi_language_current"] = "", ["name"] = item.Name, ["panel_id"] = "", ["platform"] = "all",
                ["resource_id"] = item.ResourceId, ["path"] = item.Path, ["source_platform"] = 1, ["sub_type"] = "none",
                ["time_range"] = null, ["type"] = "filter", ["value"] = intensity, ["version"] = "",
            });
            ttype = "filter";
        }
        else
        {
            id = NewMaterial("video_effects", new JsonObject
            {
                ["adjust_params"] = new JsonArray(), ["apply_target_type"] = 2, ["apply_time_range"] = null, ["category_id"] = "",
                ["category_name"] = "", ["common_keyframes"] = new JsonArray(), ["disable_effect_faces"] = new JsonArray(),
                ["effect_id"] = item.EffectId, ["formula_id"] = "", ["name"] = item.Name, ["platform"] = "all",
                ["render_index"] = 11000, ["resource_id"] = item.ResourceId, ["path"] = item.Path, ["source_platform"] = 0,
                ["time_range"] = null, ["track_render_index"] = 0, ["type"] = "video_effect", ["value"] = 1.0, ["version"] = "",
            });
            ttype = "effect";
        }
        var tr = PickTrack(ttype, st, du);
        var seg = BaseSegment(id, st, du, null, [], false, RenderIndex(tr));
        tr["segments"]!.AsArray().Add(seg);
        SortTrack(tr);
        return DescribeId(seg.Str("id"));
    }

    JsonNode AddMotion(Args a)
    {
        var ids = IdsOrAllMain(a, photosOnly: true);
        var preset = a.S("preset", "zoom_in");
        string[] cycle = ["zoom_in", "pan_left", "zoom_out", "pan_right"];
        double strength = a.D("strength", 0.15);
        var res = new JsonArray();
        for (int n = 0; n < ids.Count; n++)
        {
            var p = preset == "alternate" ? cycle[n % 4] : preset;
            var (_, s, _) = FindSegment(ids[n]);
            if (s["clip"] is not JsonObject clip) throw new EditException("Only visual segments can move");
            long dd = Dur(s);
            double bs = clip["scale"].Dbl("x", 1), bx = clip["transform"].Dbl("x"), by = clip["transform"].Dbl("y"), z = 1 + strength, h = strength / 2;
            var props = new Dictionary<string, (double, double)>();
            switch (p)
            {
                case "zoom_in": props["KFTypeScaleX"] = (bs, bs * z); props["KFTypeScaleY"] = (bs, bs * z); break;
                case "zoom_out": props["KFTypeScaleX"] = (bs * z, bs); props["KFTypeScaleY"] = (bs * z, bs); break;
                case "pan_left": case "pan_right": case "pan_up": case "pan_down":
                    props["KFTypeScaleX"] = (bs * z, bs * z); props["KFTypeScaleY"] = (bs * z, bs * z);
                    if (p == "pan_left") props["KFTypePositionX"] = (bx + h, bx - h);
                    if (p == "pan_right") props["KFTypePositionX"] = (bx - h, bx + h);
                    if (p == "pan_up") props["KFTypePositionY"] = (by - h, by + h);
                    if (p == "pan_down") props["KFTypePositionY"] = (by + h, by - h);
                    break;
                case "fade_in_out": case "none": break;
                default: throw new EditException("preset must be zoom_in, zoom_out, pan_left, pan_right, pan_up, pan_down, fade_in_out, alternate or none");
            }
            var kfs = s.Arr("common_keyframes");
            foreach (var k in kfs.ToList())
                if (k.Str("property_type") is "KFTypeScaleX" or "KFTypeScaleY" or "KFTypePositionX" or "KFTypePositionY" or "KFTypeAlpha" or "UNIFORM_SCALE")
                    kfs.Remove(k);
            if (p == "none") { res.Add("motion removed"); continue; }
            if (s["uniform_scale"] is JsonObject us) us["on"] = false;
            JsonObject Kf(long t, double v) => new()
            {
                ["curveType"] = "Line", ["graphID"] = "", ["left_control"] = new JsonObject { ["x"] = 0.0, ["y"] = 0.0 },
                ["right_control"] = new JsonObject { ["x"] = 0.0, ["y"] = 0.0 }, ["id"] = Ids.CapCut(), ["time_offset"] = t,
                ["values"] = new JsonArray(v),
            };
            if (p == "fade_in_out")
            {
                long f = Math.Min(500_000, dd / 4);
                kfs.Add(new JsonObject
                {
                    ["id"] = Ids.CapCut(), ["material_id"] = "", ["property_type"] = "KFTypeAlpha",
                    ["keyframe_list"] = new JsonArray(Kf(0, 0), Kf(f, 1), Kf(dd - f, 1), Kf(dd, 0)),
                });
            }
            foreach (var (prop, (v0, v1)) in props)
                kfs.Add(new JsonObject
                {
                    ["id"] = Ids.CapCut(), ["material_id"] = "", ["property_type"] = prop,
                    ["keyframe_list"] = new JsonArray(Kf(0, v0), Kf(dd, v1)),
                });
            res.Add($"{p} on {s.Str("id")[..8]}");
        }
        return res;
    }

    JsonNode SetAudioFade(string sid, double fin, double fout)
    {
        var (_, s, _) = FindSegment(sid);
        var refs = s["extra_material_refs"]!.AsArray();
        foreach (var r in refs.ToList())
            if (FindMaterial(r!.GetValue<string>()).key == "audio_fades") refs.Remove(r);
        if (fin > 0 || fout > 0)
            refs.Add(NewMaterial("audio_fades", new JsonObject
                { ["fade_in_duration"] = T.Us(fin), ["fade_out_duration"] = T.Us(fout), ["fade_type"] = 0, ["type"] = "audio_fade" }));
        return $"fade {fin}s/{fout}s";
    }
}
