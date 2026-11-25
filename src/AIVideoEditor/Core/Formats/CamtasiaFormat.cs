using System.Globalization;
using System.Text.Json.Nodes;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core.Formats;

/// <summary>
/// TechSmith Camtasia project (.tscproj, JSON).
/// Reads and writes sourceBin assets, sceneTrack csml tracks, VMFile/IMFile/AMFile/UnifiedMedia/Callout clips,
/// transforms, audio/video fades, transitions, Ken Burns keyframes, and track attributes.
/// </summary>
public sealed class CamtasiaFormat : ITimelineFormat
{
    public string Extension => ".tscproj";

    public TimelineDoc Load(string path)
    {
        var o = Json.Load(path)?.AsObject() ?? throw new EditException("Invalid Camtasia .tscproj file");
        var baseDir = Path.GetDirectoryName(path) ?? "";

        int width = (int)Math.Round(o.Dbl("width", 1920));
        int height = (int)Math.Round(o.Dbl("height", 1080));
        double rate = o.Dbl("editRate", o.Dbl("videoFormatFrameRate", 30));
        if (rate <= 0) rate = 30;

        var doc = new TimelineDoc
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Width = width > 0 ? width : 1920,
            Height = height > 0 ? height : 1080,
            Fps = rate,
        };

        var binById = new Dictionary<long, BinEntry>();
        foreach (var item in o["sourceBin"]?.AsArray() ?? [])
        {
            if (item is not JsonObject bo) continue;
            long id = bo.Long("id", -1);
            if (id < 0) continue;
            var rawSrc = bo.Str("src");
            var resolved = ResolvePath(baseDir, rawSrc);
            int mw = 0, mh = 0;
            if (bo["rect"] is JsonArray rect && rect.Count >= 4)
            {
                mw = (int)Math.Round(Val(rect[2], 0));
                mh = (int)Math.Round(Val(rect[3], 0));
            }
            long srcDurTicks = 0;
            bool hasAudio = false, isStill = false;
            foreach (var st in bo["sourceTracks"]?.AsArray() ?? [])
            {
                if (st is not JsonObject sto) continue;
                var range = sto["range"]?.AsArray();
                long len = range is { Count: >= 2 } ? (long)Math.Round(Val(range[1], 0)) : 0;
                if (len > srcDurTicks) srcDurTicks = len;
                var meta = sto["metaData"]?.ToString() ?? "";
                if (meta.Contains(".mp3", StringComparison.OrdinalIgnoreCase)
                    || meta.Contains(".wav", StringComparison.OrdinalIgnoreCase)
                    || sto["trackIndex"]?.GetValue<int>() == 1)
                {
                    if (sto.Str("metaData").Length > 0 || (bo["sourceTracks"]?.AsArray().Count ?? 0) > 1)
                        hasAudio = true;
                }
                if (sto.Long("type") == 2) isStill = true;
                if (sto.Long("type") == 1) hasAudio = true;
            }
            binById[id] = new BinEntry(id, resolved, mw, mh, Us(srcDurTicks, rate), hasAudio, isStill);
        }

        var timeline = o["timeline"]?.AsObject();
        var attrs = timeline?["trackAttributes"]?.AsArray() ?? [];
        var tracksArr = timeline?["sceneTrack"]?["scenes"]?[0]?["csml"]?["tracks"]?.AsArray() ?? [];

        var loadedTracks = new List<TTrack>();
        for (int ti = 0; ti < tracksArr.Count; ti++)
        {
            if (tracksArr[ti] is not JsonObject trObj) continue;
            var attr = ti < attrs.Count ? attrs[ti] as JsonObject : null;
            var track = new TTrack
            {
                Name = attr.Str("ident"),
                Muted = attr.Bool("audioMuted"),
                Hidden = attr.Bool("videoHidden"),
            };

            var clipsByMediaId = new Dictionary<long, TClip>();
            foreach (var mNode in trObj["medias"]?.AsArray() ?? [])
            {
                if (mNode is not JsonObject m) continue;
                var clip = ParseMedia(m, doc, rate, binById, out long mediaId);
                if (clip == null) continue;
                clip.Extra["tsc"] = m.ToJsonString();
                if (mediaId > 0)
                {
                    clip.Extra["tsc_id"] = mediaId.ToString(CultureInfo.InvariantCulture);
                    clipsByMediaId[mediaId] = clip;
                }
                track.Clips.Add(clip);
            }

            foreach (var trNode in trObj["transitions"]?.AsArray() ?? [])
            {
                if (trNode is not JsonObject tr) continue;
                long leftId = tr.Long("leftMedia", -1);
                if (leftId > 0 && clipsByMediaId.TryGetValue(leftId, out var leftClip))
                {
                    leftClip.Transition = MapTransitionFromCamtasia(tr.Str("name"));
                    leftClip.TransitionDur = Us(tr.Dbl("duration", rate * 0.5), rate);
                }
            }

            track.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (track.Clips.Count > 0 && track.Clips.All(c => c.Kind == "audio")) track.Type = "audio";
            else if (track.Clips.Count > 0 && track.Clips.All(c => c.Kind == "text")) track.Type = "text";
            else track.Type = "video";

            loadedTracks.Add(track);
        }

        // Camtasia draws higher trackIndex on top; our TimelineModel keeps the Main video track at index 0,
        // overlay/text tracks above it when reversed for display, and audio tracks together.
        var main = loadedTracks.FirstOrDefault(t => t.Type == "video");
        if (main != null)
        {
            doc.Tracks.Add(main);
            foreach (var t in loadedTracks.Where(x => x != main && x.Type != "audio")) doc.Tracks.Add(t);
            foreach (var t in loadedTracks.Where(x => x != main && x.Type == "audio")) doc.Tracks.Add(t);
        }
        else
        {
            doc.Tracks.AddRange(loadedTracks);
        }

        return doc;
    }

    public void Save(TimelineDoc doc, string path)
    {
        JsonObject root;
        try { root = File.Exists(path) ? Json.Load(path)!.AsObject() : Skeleton(doc); }
        catch { root = Skeleton(doc); }

        double rate = doc.Fps > 0 ? doc.Fps : 30;
        root["width"] = doc.Width;
        root["height"] = doc.Height;
        root["editRate"] = Math.Round(rate);
        root["videoFormatFrameRate"] = Math.Round(rate);
        if (root["title"] == null || root["title"]!.ToString() == "")
            root["title"] = doc.Name;

        // Track existing sourceBin entries by normalized path so unmodified asset IDs survive.
        var sourceBin = new JsonArray();
        var binByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long nextId = 1;

        foreach (var existing in root["sourceBin"]?.AsArray() ?? [])
        {
            if (existing is not JsonObject bo) continue;
            long id = bo.Long("id", 0);
            if (id >= nextId) nextId = id + 1;
            var src = bo.Str("src");
            if (!string.IsNullOrEmpty(src) && !binByPath.ContainsKey(src))
            {
                binByPath[src] = id;
                sourceBin.Add(bo.DeepClone());
            }
        }

        // Reserve IDs already present on the timeline or clips so nothing collides.
        foreach (var t in doc.Tracks)
        {
            foreach (var c in t.Clips)
            {
                if (c.Extra.TryGetValue("tsc_id", out var sid) && long.TryParse(sid, out var cid) && cid >= nextId)
                    nextId = cid + 1;
            }
        }
        long AllocId() => nextId++;

        long EnsureSourceBin(TClip c)
        {
            var p = c.Path;
            if (binByPath.TryGetValue(p, out var existingId)) return existingId;

            long id = AllocId();
            binByPath[p] = id;
            int w = c.MediaWidth > 0 ? c.MediaWidth : doc.Width;
            int h = c.MediaHeight > 0 ? c.MediaHeight : doc.Height;
            long ticks = c.Kind == "photo" ? 1 : Math.Max(1, Ticks(c.SourceDur > 0 ? c.SourceDur : c.Dur, rate));

            var srcTracks = new JsonArray();
            if (c.Kind == "audio")
            {
                srcTracks.Add(new JsonObject
                {
                    ["range"] = new JsonArray(0, ticks),
                    ["type"] = 1,
                    ["editRate"] = Math.Round(rate),
                    ["trackRect"] = new JsonArray(0, 0, 0, 0),
                    ["sampleRate"] = 44100,
                    ["bitDepth"] = 16,
                    ["numChannels"] = 2,
                    ["integratedLUFS"] = -14.0,
                    ["metaData"] = Path.GetFileName(p),
                });
            }
            else if (c.Kind == "photo")
            {
                srcTracks.Add(new JsonObject
                {
                    ["range"] = new JsonArray(0, 1),
                    ["type"] = 2,
                    ["editRate"] = Math.Round(rate),
                    ["trackRect"] = new JsonArray(0, 0, w, h),
                    ["sampleRate"] = 0,
                    ["bitDepth"] = 0,
                    ["numChannels"] = 0,
                    ["metaData"] = Path.GetFileName(p),
                });
            }
            else
            {
                srcTracks.Add(new JsonObject
                {
                    ["range"] = new JsonArray(0, ticks),
                    ["type"] = 0,
                    ["editRate"] = Math.Round(rate),
                    ["trackRect"] = new JsonArray(0, 0, w, h),
                    ["sampleRate"] = 0,
                    ["bitDepth"] = 0,
                    ["numChannels"] = 0,
                    ["metaData"] = Path.GetFileName(p),
                });
                if (c.HasAudio)
                {
                    srcTracks.Add(new JsonObject
                    {
                        ["range"] = new JsonArray(0, ticks),
                        ["type"] = 1,
                        ["editRate"] = Math.Round(rate),
                        ["trackRect"] = new JsonArray(0, 0, 0, 0),
                        ["sampleRate"] = 44100,
                        ["bitDepth"] = 16,
                        ["numChannels"] = 2,
                        ["metaData"] = Path.GetFileName(p),
                    });
                }
            }

            sourceBin.Add(new JsonObject
            {
                ["id"] = id,
                ["src"] = p,
                ["rect"] = new JsonArray(0, 0, w, h),
                ["lastMod"] = File.Exists(p) ? File.GetLastWriteTimeUtc(p).ToString("yyyyMMddTHHmmss") : "",
                ["sourceTracks"] = srcTracks,
            });
            return id;
        }

        // Order Camtasia tracks bottom-to-top: audio first, then visual main -> overlays -> text.
        var audioTracks = doc.Tracks.Where(t => t.Type == "audio").ToList();
        var visualTracks = doc.Tracks.Where(t => t.Type != "audio").ToList();
        var ordered = audioTracks.Concat(visualTracks).ToList();

        var csmlTracks = new JsonArray();
        var trackAttrs = new JsonArray();
        long maxTimelineEnd = 0;

        for (int ti = 0; ti < ordered.Count; ti++)
        {
            var t = ordered[ti];
            var medias = new JsonArray();
            var transitions = new JsonArray();
            var list = t.Clips.OrderBy(c => c.Start).ToList();
            var mediaIds = new List<long>(list.Count);

            for (int ci = 0; ci < list.Count; ci++)
            {
                var c = list[ci];
                long mid = c.Extra.TryGetValue("tsc_id", out var sid) && long.TryParse(sid, out var parsedId)
                    ? parsedId
                    : AllocId();
                c.Extra["tsc_id"] = mid.ToString(CultureInfo.InvariantCulture);
                mediaIds.Add(mid);

                long startTicks = Ticks(c.Start, rate);
                long durTicks = Math.Max(1, Ticks(c.Dur, rate));
                if (startTicks + durTicks > maxTimelineEnd) maxTimelineEnd = startTicks + durTicks;

                var mediaObj = BuildMediaObject(c, mid, doc, rate, EnsureSourceBin, AllocId);
                medias.Add(mediaObj);
            }

            for (int ci = 0; ci < list.Count; ci++)
            {
                var c = list[ci];
                if (string.IsNullOrEmpty(c.Transition)) continue;
                long durTicks = Math.Max(1, Ticks(c.TransitionDur > 0 ? c.TransitionDur : 500_000, rate));
                var trObj = new JsonObject
                {
                    ["name"] = MapTransitionToCamtasia(c.Transition),
                    ["duration"] = durTicks,
                    ["leftMedia"] = mediaIds[ci],
                    ["trivial"] = false,
                    ["useAudioPreRoll"] = true,
                    ["useVisualPreRoll"] = true,
                };
                if (ci + 1 < mediaIds.Count) trObj["rightMedia"] = mediaIds[ci + 1];
                transitions.Add(trObj);
            }

            csmlTracks.Add(new JsonObject
            {
                ["trackIndex"] = ti,
                ["medias"] = medias,
                ["transitions"] = transitions,
            });
            trackAttrs.Add(new JsonObject
            {
                ["type"] = 0,
                ["ident"] = string.IsNullOrWhiteSpace(t.Name) ? $"Track {ti + 1}" : t.Name,
                ["audioMuted"] = t.Muted,
                ["videoHidden"] = t.Hidden,
                ["magnetic"] = false,
                ["matte"] = 0,
                ["solo"] = false,
            });
        }

        root["sourceBin"] = sourceBin;
        if (root["timeline"] is not JsonObject tl)
        {
            tl = new JsonObject();
            root["timeline"] = tl;
        }
        tl["id"] = tl.Long("id", AllocId());
        tl["trackAttributes"] = trackAttrs;

        if (tl["sceneTrack"] is not JsonObject sceneTrack)
        {
            sceneTrack = new JsonObject();
            tl["sceneTrack"] = sceneTrack;
        }
        if (sceneTrack["scenes"] is not JsonArray scenes)
        {
            scenes = new JsonArray();
            sceneTrack["scenes"] = scenes;
        }
        if (scenes.Count == 0 || scenes[0] is not JsonObject scene0)
        {
            scene0 = new JsonObject();
            if (scenes.Count == 0) scenes.Add(scene0);
            else scenes[0] = scene0;
        }
        scene0["duration"] = maxTimelineEnd;
        scene0["title"] = "";
        scene0["type"] = 0;
        if (scene0["csml"] is not JsonObject csml)
        {
            csml = new JsonObject();
            scene0["csml"] = csml;
        }
        csml["tracks"] = csmlTracks;

        Json.SaveAtomic(path, root.ToJsonString(Json.Pretty));
    }

    JsonObject BuildMediaObject(
        TClip c,
        long mid,
        TimelineDoc doc,
        double rate,
        Func<TClip, long> ensureBin,
        Func<long> allocId)
    {
        long startTicks = Ticks(c.Start, rate);
        long durTicks = Math.Max(1, Ticks(c.Dur, rate));
        long inTicks = Ticks(c.In, rate);
        double scalar = c.Speed > 0 ? Math.Round(1.0 / c.Speed, 6) : 1.0;

        JsonObject m = c.Kind switch
        {
            "text" => BuildCallout(c, mid, doc, startTicks, durTicks, rate),
            "audio" => BuildAudioMedia(c, mid, ensureBin(c), startTicks, durTicks, inTicks, scalar, rate),
            "photo" => BuildImageMedia(c, mid, ensureBin(c), doc, startTicks, durTicks, rate),
            _ => c.HasAudio
                ? BuildUnifiedVideoMedia(c, mid, ensureBin(c), doc, startTicks, durTicks, inTicks, scalar, rate, allocId)
                : BuildVideoOnlyMedia(c, mid, ensureBin(c), doc, startTicks, durTicks, inTicks, scalar, rate),
        };

        // Store non-native timeline attributes (adjustment/reverse/fade/motion/id) in attributes metadata so round-trips stay exact.
        var attrs = m["attributes"] as JsonObject ?? new JsonObject();
        attrs["ident"] = c.Kind == "text" ? "Callout" : Path.GetFileNameWithoutExtension(c.Path);
        attrs["aiveMeta"] = SaveMeta(c);
        m["attributes"] = attrs;
        return m;
    }

    static JsonObject BuildCallout(TClip c, long mid, TimelineDoc doc, long startTicks, long durTicks, double rate)
    {
        var p = BuildVisualParams(c, doc, durTicks, rate);
        var (fr, fg, fb, fa) = ParseHexRgba(c.Color);
        var def = new JsonObject
        {
            ["kind"] = "remix",
            ["shape"] = "text",
            ["style"] = "basic",
            ["text"] = c.Text ?? "",
            ["font"] = new JsonObject
            {
                ["color-blue"] = Math.Round(fb, 4),
                ["color-green"] = Math.Round(fg, 4),
                ["color-red"] = Math.Round(fr, 4),
                ["color-alpha"] = Math.Round(fa, 4),
                ["size"] = Math.Round(c.FontSize * 8 * (doc.Height / 1080.0), 1),
                ["tracking"] = c.LetterSpacing,
                ["name"] = string.IsNullOrEmpty(c.Font) ? "Arial" : c.Font,
                ["weight"] = c.Bold ? "Bold" : "Regular",
            },
            ["horizontal-alignment"] = c.Align,
            ["vertical-alignment"] = "center",
            ["text-attributes"] = new JsonObject
            {
                ["type"] = "textAttributeList",
                ["keyframes"] = new JsonArray(),
            },
        };
        return new JsonObject
        {
            ["id"] = mid,
            ["_type"] = "Callout",
            ["def"] = def,
            ["start"] = startTicks,
            ["duration"] = durTicks,
            ["mediaStart"] = 0,
            ["mediaDuration"] = durTicks,
            ["scalar"] = 1,
            ["parameters"] = p,
            ["effects"] = new JsonArray(),
        };
    }

    static JsonObject BuildAudioMedia(TClip c, long mid, long srcId, long startTicks, long durTicks, long inTicks, double scalar, double rate)
    {
        return new JsonObject
        {
            ["id"] = mid,
            ["_type"] = "AMFile",
            ["src"] = srcId,
            ["trackNumber"] = 0,
            ["start"] = startTicks,
            ["duration"] = durTicks,
            ["mediaStart"] = inTicks,
            ["mediaDuration"] = durTicks,
            ["scalar"] = scalar,
            ["parameters"] = new JsonObject
            {
                ["volume"] = BuildVolumeParam(c, durTicks, rate),
            },
            ["effects"] = new JsonArray(),
        };
    }

    static JsonObject BuildImageMedia(TClip c, long mid, long srcId, TimelineDoc doc, long startTicks, long durTicks, double rate)
    {
        var p = BuildVisualParams(c, doc, durTicks, rate);
        p["geometryCrop0"] = c.CropLeft;
        p["geometryCrop1"] = c.CropTop;
        p["geometryCrop2"] = c.CropRight;
        p["geometryCrop3"] = c.CropBottom;
        return new JsonObject
        {
            ["id"] = mid,
            ["_type"] = "IMFile",
            ["src"] = srcId,
            ["trackNumber"] = 0,
            ["start"] = startTicks,
            ["duration"] = durTicks,
            ["mediaStart"] = 0,
            ["mediaDuration"] = 1,
            ["scalar"] = 1,
            ["trimStartSum"] = 0,
            ["parameters"] = p,
            ["effects"] = new JsonArray(),
        };
    }

    static JsonObject BuildVideoOnlyMedia(TClip c, long mid, long srcId, TimelineDoc doc, long startTicks, long durTicks, long inTicks, double scalar, double rate)
    {
        var p = BuildVisualParams(c, doc, durTicks, rate);
        p["geometryCrop0"] = c.CropLeft;
        p["geometryCrop1"] = c.CropTop;
        p["geometryCrop2"] = c.CropRight;
        p["geometryCrop3"] = c.CropBottom;
        return new JsonObject
        {
            ["id"] = mid,
            ["_type"] = "VMFile",
            ["src"] = srcId,
            ["trackNumber"] = 0,
            ["start"] = startTicks,
            ["duration"] = durTicks,
            ["mediaStart"] = inTicks,
            ["mediaDuration"] = durTicks,
            ["scalar"] = scalar,
            ["parameters"] = p,
            ["effects"] = new JsonArray(),
        };
    }

    static JsonObject BuildUnifiedVideoMedia(TClip c, long mid, long srcId, TimelineDoc doc, long startTicks, long durTicks, long inTicks, double scalar, double rate, Func<long> allocId)
    {
        var vp = BuildVisualParams(c, doc, durTicks, rate);
        vp["geometryCrop0"] = c.CropLeft;
        vp["geometryCrop1"] = c.CropTop;
        vp["geometryCrop2"] = c.CropRight;
        vp["geometryCrop3"] = c.CropBottom;

        return new JsonObject
        {
            ["id"] = mid,
            ["_type"] = "UnifiedMedia",
            ["start"] = startTicks,
            ["duration"] = durTicks,
            ["mediaStart"] = inTicks,
            ["mediaDuration"] = durTicks,
            ["scalar"] = scalar,
            ["video"] = new JsonObject
            {
                ["id"] = allocId(),
                ["_type"] = "VMFile",
                ["src"] = srcId,
                ["trackNumber"] = 0,
                ["start"] = startTicks,
                ["duration"] = durTicks,
                ["mediaStart"] = inTicks,
                ["mediaDuration"] = durTicks,
                ["scalar"] = scalar,
                ["parameters"] = vp,
            },
            ["audio"] = new JsonObject
            {
                ["id"] = allocId(),
                ["_type"] = "AMFile",
                ["src"] = srcId,
                ["trackNumber"] = 1,
                ["start"] = startTicks,
                ["duration"] = durTicks,
                ["mediaStart"] = inTicks,
                ["mediaDuration"] = durTicks,
                ["scalar"] = scalar,
                ["parameters"] = new JsonObject
                {
                    ["volume"] = BuildVolumeParam(c, durTicks, rate),
                },
            },
            ["effects"] = new JsonArray(),
        };
    }

    static JsonObject BuildVisualParams(TClip c, TimelineDoc doc, long durTicks, double rate)
    {
        var p = new JsonObject();
        double baseTx = c.X * (doc.Width / 2.0);
        double baseTy = c.Y * (doc.Height / 2.0);
        double sx = c.Scale * (c.FlipH ? -1 : 1);
        double sy = c.Scale * (c.FlipV ? -1 : 1);
        double rad = c.Rotation * Math.PI / 180.0;

        if (c.Motion is { } motion)
        {
            double z = 1 + c.MotionStrength;
            double hx = c.MotionStrength * (doc.Width / 4.0);
            double hy = c.MotionStrength * (doc.Height / 4.0);
            (double s0, double x0, double y0, double s1, double x1, double y1) = motion switch
            {
                "zoom_in" => (1.0, baseTx, baseTy, z, baseTx, baseTy),
                "zoom_out" => (z, baseTx, baseTy, 1.0, baseTx, baseTy),
                "pan_left" => (z, baseTx + hx, baseTy, z, baseTx - hx, baseTy),
                "pan_right" => (z, baseTx - hx, baseTy, z, baseTx + hx, baseTy),
                "pan_up" => (z, baseTx, baseTy - hy, z, baseTx, baseTy + hy),
                "pan_down" => (z, baseTx, baseTy + hy, z, baseTx, baseTy - hy),
                _ => (1.0, baseTx, baseTy, 1.0, baseTx, baseTy),
            };
            p["translation0"] = AnimatedDouble((0, x0), (durTicks, x1));
            p["translation1"] = AnimatedDouble((0, y0), (durTicks, y1));
            p["scale0"] = AnimatedDouble((0, s0 * sx), (durTicks, s1 * sx));
            p["scale1"] = AnimatedDouble((0, s0 * sy), (durTicks, s1 * sy));
        }
        else
        {
            p["translation0"] = baseTx;
            p["translation1"] = baseTy;
            p["scale0"] = sx;
            p["scale1"] = sy;
        }

        p["rotation0"] = 0;
        p["rotation1"] = 0;
        p["rotation2"] = Math.Round(rad, 6);

        if (c.VideoFadeIn > 0 || c.VideoFadeOut > 0)
        {
            var pts = new List<(long t, double v)>();
            long fin = Ticks(c.VideoFadeIn, rate);
            long fout = Ticks(c.VideoFadeOut, rate);
            if (fin > 0) { pts.Add((0, 0)); pts.Add((Math.Min(durTicks, fin), c.Opacity)); }
            else pts.Add((0, c.Opacity));
            if (fout > 0) { pts.Add((Math.Max(0, durTicks - fout), c.Opacity)); pts.Add((durTicks, 0)); }
            p["opacity"] = AnimatedDouble(pts.ToArray());
        }
        else
        {
            p["opacity"] = c.Opacity;
        }

        return p;
    }

    static JsonNode BuildVolumeParam(TClip c, long durTicks, double rate)
    {
        if (c.AudioFadeIn <= 0 && c.AudioFadeOut <= 0) return c.Volume;
        var pts = new List<(long t, double v)>();
        long fin = Ticks(c.AudioFadeIn, rate);
        long fout = Ticks(c.AudioFadeOut, rate);
        if (fin > 0) { pts.Add((0, 0)); pts.Add((Math.Min(durTicks, fin), c.Volume)); }
        else pts.Add((0, c.Volume));
        if (fout > 0) { pts.Add((Math.Max(0, durTicks - fout), c.Volume)); pts.Add((durTicks, 0)); }
        return AnimatedDouble(pts.ToArray());
    }

    static JsonObject AnimatedDouble(params (long time, double value)[] pts) => new()
    {
        ["type"] = "double",
        ["defaultValue"] = pts.Length > 0 ? pts[0].value : 1.0,
        ["keyframes"] = new JsonArray(pts.Select(pt => (JsonNode)new JsonObject
        {
            ["endTime"] = pt.time,
            ["time"] = pt.time,
            ["value"] = Math.Round(pt.value, 6),
            ["duration"] = 0,
            ["interp"] = "linr",
        }).ToArray()),
    };

    TClip? ParseMedia(JsonObject m, TimelineDoc doc, double rate, Dictionary<long, BinEntry> binById, out long mediaId)
    {
        mediaId = m.Long("id", -1);
        var type = m.Str("_type");
        var attrs = m["attributes"] as JsonObject;
        var meta = LoadMeta(attrs?["aiveMeta"]);

        JsonObject? innerVideo = m["video"] as JsonObject;
        JsonObject? innerAudio = m["audio"] as JsonObject;
        long srcId = m.Long("src", -1);
        if (srcId < 0 && innerVideo != null) srcId = innerVideo.Long("src", -1);
        if (srcId < 0 && innerAudio != null) srcId = innerAudio.Long("src", -1);

        binById.TryGetValue(srcId, out var bin);

        var startUs = Us(m.Dbl("start"), rate);
        var durUs = Us(m.Dbl("duration"), rate);
        var inUs = Us(m.Dbl("mediaStart"), rate);
        double scalar = m.Dbl("scalar", 1.0);
        if (scalar <= 0) scalar = 1.0;

        string kind;
        if (type == "Callout") kind = "text";
        else if (type == "AMFile") kind = "audio";
        else if (type == "IMFile" || bin?.IsStill == true) kind = "photo";
        else kind = "video";

        var vp = (innerVideo?["parameters"] ?? m["parameters"]) as JsonObject;
        var ap = (innerAudio?["parameters"] ?? m["parameters"]) as JsonObject;

        double sx = ParamVal(vp?["scale0"], 1.0);
        double sy = ParamVal(vp?["scale1"], 1.0);
        double absScale = Math.Abs(sx) > 0.001 ? Math.Abs(sx) : 1.0;

        var clip = new TClip
        {
            Id = meta?.Str("id") is { Length: > 0 } savedId ? savedId : Ids.Short(),
            Kind = kind,
            Path = bin?.Src ?? "",
            Start = startUs,
            Dur = durUs,
            In = inUs,
            SourceDur = bin?.SourceDurUs ?? 0,
            MediaWidth = bin?.Width ?? doc.Width,
            MediaHeight = bin?.Height ?? doc.Height,
            HasAudio = type == "UnifiedMedia" || type == "AMFile" || (bin?.HasAudio ?? false),
            Speed = meta?.Dbl("speed", Math.Round(1.0 / scalar, 4)) ?? Math.Round(1.0 / scalar, 4),
            X = ParamVal(vp?["translation0"], 0) / Math.Max(doc.Width / 2.0, 1.0),
            Y = ParamVal(vp?["translation1"], 0) / Math.Max(doc.Height / 2.0, 1.0),
            Scale = absScale,
            FlipH = sx < 0,
            FlipV = sy < 0,
            Rotation = ParamVal(vp?["rotation2"], 0) * 180.0 / Math.PI,
            Opacity = Math.Clamp(ParamVal(vp?["opacity"], 1.0), 0.0, 1.0),
            Volume = Math.Clamp(ParamVal(ap?["volume"], 1.0), 0.0, 4.0),
            CropLeft = vp.Dbl("geometryCrop0", 0),
            CropTop = vp.Dbl("geometryCrop1", 0),
            CropRight = vp.Dbl("geometryCrop2", 0),
            CropBottom = vp.Dbl("geometryCrop3", 0),
        };

        if (kind == "text")
        {
            var def = m["def"] as JsonObject;
            clip.Text = def.Str("text");
            if (def?["font"] is JsonObject fnt)
            {
                clip.Font = fnt.Str("name");
                clip.Bold = fnt.Str("weight").Contains("Bold", StringComparison.OrdinalIgnoreCase);
                double fpx = fnt.Dbl("size", 64);
                clip.FontSize = Math.Round(fpx / Math.Max(8.0 * (doc.Height / 1080.0), 1.0), 2);
                clip.Color = RgbaToHex(
                    fnt.Dbl("color-red", 1),
                    fnt.Dbl("color-green", 1),
                    fnt.Dbl("color-blue", 1));
                clip.LetterSpacing = fnt.Dbl("tracking", 0);
            }
            var align = def.Str("horizontal-alignment");
            if (align is "left" or "center" or "right") clip.Align = align;
        }

        if (meta != null) ApplyMeta(clip, meta);
        return clip;
    }

    static JsonObject SaveMeta(TClip c)
    {
        var o = new JsonObject
        {
            ["id"] = c.Id,
            ["speed"] = c.Speed,
            ["scale"] = c.Scale,
            ["reverse"] = c.Reverse,
            ["keepPitch"] = c.KeepPitch,
            ["blend"] = c.BlendMode,
            ["blur"] = c.CanvasBlur,
            ["canvasColor"] = c.CanvasColor ?? "",
            ["cornerRadius"] = c.CornerRadius,
            ["vfin"] = c.VideoFadeIn,
            ["vfout"] = c.VideoFadeOut,
            ["afin"] = c.AudioFadeIn,
            ["afout"] = c.AudioFadeOut,
            ["motion"] = c.Motion ?? "",
            ["motionStr"] = c.MotionStrength,
            ["bright"] = c.Brightness,
            ["contrast"] = c.Contrast,
            ["sat"] = c.Saturation,
            ["temp"] = c.Temperature,
            ["hue"] = c.Hue,
            ["sharp"] = c.Sharpen,
            ["hi"] = c.Highlights,
            ["sh"] = c.Shadows,
            ["vig"] = c.Vignette,
            ["fade"] = c.ColorFade,
            ["pan"] = c.AudioPan,
            ["nr"] = c.NoiseReduction,
            ["iso"] = c.VocalIsolation,
            ["italic"] = c.Italic,
            ["stroke"] = c.StrokeColor ?? "",
            ["strokeW"] = c.StrokeWidth,
            ["bg"] = c.BackgroundColor ?? "",
            ["bgA"] = c.BackgroundAlpha,
            ["shadow"] = c.Shadow,
            ["lineSp"] = c.LineSpacing,
        };
        if (c.Keyframes.Count > 0)
        {
            o["kfs"] = new JsonArray(c.Keyframes.Select(k => (JsonNode)new JsonObject
            {
                ["t"] = k.TimeUs,
                ["sc"] = k.Scale,
                ["x"] = k.X,
                ["y"] = k.Y,
                ["r"] = k.Rotation,
                ["op"] = k.Opacity,
            }).ToArray());
        }
        return o;
    }

    static JsonObject? LoadMeta(JsonNode? n) => n as JsonObject;

    static void ApplyMeta(TClip c, JsonObject m)
    {
        if (m["scale"] != null) c.Scale = m.Dbl("scale", c.Scale);
        c.Reverse = m.Bool("reverse");
        c.KeepPitch = m.Bool("keepPitch");
        c.BlendMode = m.Str("blend", "normal");
        c.CanvasBlur = m.Dbl("blur");
        var cc = m.Str("canvasColor");
        c.CanvasColor = cc == "" ? null : cc;
        c.CornerRadius = m.Dbl("cornerRadius");
        c.VideoFadeIn = m.Long("vfin");
        c.VideoFadeOut = m.Long("vfout");
        c.AudioFadeIn = m.Long("afin");
        c.AudioFadeOut = m.Long("afout");
        var mot = m.Str("motion");
        c.Motion = mot == "" ? null : mot;
        c.MotionStrength = m.Dbl("motionStr", 0.15);
        c.Brightness = m.Dbl("bright");
        c.Contrast = m.Dbl("contrast");
        c.Saturation = m.Dbl("sat");
        c.Temperature = m.Dbl("temp");
        c.Hue = m.Dbl("hue");
        c.Sharpen = m.Dbl("sharp");
        c.Highlights = m.Dbl("hi");
        c.Shadows = m.Dbl("sh");
        c.Vignette = m.Dbl("vig");
        c.ColorFade = m.Dbl("fade");
        c.AudioPan = m.Dbl("pan");
        c.NoiseReduction = m.Bool("nr");
        c.VocalIsolation = m.Str("iso", "none");
        c.Italic = m.Bool("italic");
        var st = m.Str("stroke");
        c.StrokeColor = st == "" ? null : st;
        c.StrokeWidth = m.Dbl("strokeW", 0.08);
        var bg = m.Str("bg");
        c.BackgroundColor = bg == "" ? null : bg;
        c.BackgroundAlpha = m.Dbl("bgA", 0.6);
        c.Shadow = m.Bool("shadow");
        c.LineSpacing = m.Dbl("lineSp", 0.02);

        if (m["kfs"] is JsonArray kfs)
        {
            c.Keyframes.Clear();
            foreach (var kn in kfs)
            {
                if (kn is not JsonObject ko) continue;
                c.Keyframes.Add(new TKeyframe
                {
                    TimeUs = ko.Long("t"),
                    Scale = ko["sc"] != null ? ko.Dbl("sc") : null,
                    X = ko["x"] != null ? ko.Dbl("x") : null,
                    Y = ko["y"] != null ? ko.Dbl("y") : null,
                    Rotation = ko["r"] != null ? ko.Dbl("r") : null,
                    Opacity = ko["op"] != null ? ko.Dbl("op") : null,
                });
            }
        }
    }

    static double ParamVal(JsonNode? n, double def)
    {
        if (n == null) return def;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            return def;
        }
        if (n is JsonObject o)
        {
            if (o["keyframes"] is JsonArray kfs && kfs.Count > 0 && kfs[0] is JsonObject k0)
                return Val(k0["value"], o.Dbl("defaultValue", def));
            return o.Dbl("defaultValue", def);
        }
        return def;
    }

    static double Val(JsonNode? n, double def)
    {
        if (n is not JsonValue v) return def;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return def;
    }

    static string ResolvePath(string baseDir, string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return "";
        var clean = src.Replace('/', '\\');
        if (Path.IsPathRooted(clean) || string.IsNullOrEmpty(baseDir)) return clean;
        return Path.GetFullPath(Path.Combine(baseDir, clean));
    }

    static long Ticks(long us, double rate) => (long)Math.Round(us / 1_000_000.0 * rate);
    static long Us(double ticks, double rate) => (long)Math.Round(ticks / rate * 1_000_000.0);

    static string MapTransitionToCamtasia(string name) => name.Trim().ToLowerInvariant() switch
    {
        var n when n.Contains("black") => "FadeThroughBlack",
        var n when n.Contains("white") => "FadeThroughWhite",
        var n when n.Contains("wipe") || n.Contains("slide") => "SlideRight",
        _ => "Fade",
    };

    static string MapTransitionFromCamtasia(string name) => name.Trim().ToLowerInvariant() switch
    {
        var n when n.Contains("black") => "Dip to Black",
        var n when n.Contains("white") => "Dip to White",
        var n when n.Contains("slide") || n.Contains("wipe") => "Wipe",
        _ => "Cross Dissolve",
    };

    static (double r, double g, double b, double a) ParseHexRgba(string hex)
    {
        hex = (hex ?? "#FFFFFF").Trim().TrimStart('#');
        if (hex.Length >= 6
            && int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r)
            && int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g)
            && int.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
        {
            return (r / 255.0, g / 255.0, b / 255.0, 1.0);
        }
        return (1, 1, 1, 1);
    }

    static string RgbaToHex(double r, double g, double b)
    {
        int ri = Math.Clamp((int)Math.Round(r * 255), 0, 255);
        int gi = Math.Clamp((int)Math.Round(g * 255), 0, 255);
        int bi = Math.Clamp((int)Math.Round(b * 255), 0, 255);
        return $"#{ri:X2}{gi:X2}{bi:X2}";
    }

    static JsonObject Skeleton(TimelineDoc doc) => new()
    {
        ["title"] = doc.Name,
        ["description"] = "",
        ["author"] = "",
        ["targetLoudness"] = -18.0,
        ["shouldApplyLoudnessNormalization"] = true,
        ["videoFormatFrameRate"] = doc.IntFps,
        ["audioFormatSampleRate"] = 44100,
        ["allowSubFrameEditing"] = false,
        ["width"] = doc.Width,
        ["height"] = doc.Height,
        ["version"] = "6.0",
        ["editRate"] = doc.IntFps,
        ["authoringClientName"] = new JsonObject
        {
            ["name"] = "Camtasia",
            ["platform"] = "Windows",
            ["version"] = "2024.0.0",
        },
        ["sourceBin"] = new JsonArray(),
        ["timeline"] = new JsonObject
        {
            ["id"] = 1,
            ["sceneTrack"] = new JsonObject
            {
                ["scenes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["duration"] = 0,
                        ["title"] = "",
                        ["type"] = 0,
                        ["csml"] = new JsonObject { ["tracks"] = new JsonArray() },
                    },
                },
            },
            ["trackAttributes"] = new JsonArray(),
        },
    };

    sealed record BinEntry(long Id, string Src, int Width, int Height, long SourceDurUs, bool HasAudio, bool IsStill);
}
