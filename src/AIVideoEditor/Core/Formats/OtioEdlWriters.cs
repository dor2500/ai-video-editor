using System.Text;
using System.Text.Json.Nodes;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core.Formats;

/// <summary>OpenTimelineIO (.otio) – imported by Kdenlive (File → Import → OpenTimelineIO), DaVinci Resolve and others.</summary>
public sealed class OtioWriter : ITimelineFormat
{
    public string Extension => ".otio";
    public TimelineDoc Load(string path) => throw new EditException("OTIO is export-only");

    public void Save(TimelineDoc d, string path)
    {
        JsonObject Rt(long frames) => new() { ["OTIO_SCHEMA"] = "RationalTime.1", ["rate"] = (double)d.IntFps, ["value"] = (double)frames };
        JsonObject Range(long start, long dur) => new() { ["OTIO_SCHEMA"] = "TimeRange.1", ["start_time"] = Rt(start), ["duration"] = Rt(dur) };
        var tracks = new JsonArray();
        int v = 0, a = 0;
        foreach (var t in d.Tracks.Where(t => t.Type != "text").OrderBy(t => t.Type == "audio"))
        {
            bool audio = t.Type == "audio";
            var children = new JsonArray();
            long cur = 0;
            var clips = t.Clips.OrderBy(c => c.Start).ToList();
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                long s = d.Frames(c.Start), len = d.Frames(c.End) - s;
                if (s > cur) children.Add(new JsonObject { ["OTIO_SCHEMA"] = "Gap.1", ["name"] = "", ["source_range"] = Range(0, s - cur) });
                long inF = c.Kind == "photo" ? 0 : d.Frames(c.In);
                children.Add(new JsonObject
                {
                    ["OTIO_SCHEMA"] = "Clip.1", ["name"] = c.FileName, ["source_range"] = Range(inF, len),
                    ["media_reference"] = new JsonObject
                    {
                        ["OTIO_SCHEMA"] = "ExternalReference.1", ["target_url"] = ExportUtil.FileUrl(c.Path, false),
                        ["available_range"] = c.SourceDur > 0 ? Range(0, d.Frames(c.SourceDur)) : null, ["metadata"] = new JsonObject(),
                    },
                    ["metadata"] = new JsonObject(), ["effects"] = new JsonArray(), ["markers"] = new JsonArray(), ["enabled"] = !t.Muted,
                });
                cur = s + len;
                if (!audio && c.Transition != null && i + 1 < clips.Count && clips[i + 1].Start == c.End)
                {
                    long td = d.Frames(c.TransitionDur);
                    children.Add(new JsonObject
                    {
                        ["OTIO_SCHEMA"] = "Transition.1", ["name"] = c.Transition, ["transition_type"] = "SMPTE_Dissolve",
                        ["in_offset"] = Rt(td / 2), ["out_offset"] = Rt(td - td / 2), ["metadata"] = new JsonObject(),
                    });
                }
            }
            tracks.Add(new JsonObject
            {
                ["OTIO_SCHEMA"] = "Track.1", ["name"] = audio ? $"A{++a}" : $"V{++v}", ["kind"] = audio ? "Audio" : "Video",
                ["children"] = children, ["metadata"] = new JsonObject(), ["effects"] = new JsonArray(), ["markers"] = new JsonArray(),
                ["enabled"] = true, ["source_range"] = null,
            });
        }
        var root = new JsonObject
        {
            ["OTIO_SCHEMA"] = "Timeline.1", ["name"] = d.Name, ["global_start_time"] = null, ["metadata"] = new JsonObject(),
            ["tracks"] = new JsonObject
            {
                ["OTIO_SCHEMA"] = "Stack.1", ["name"] = "tracks", ["children"] = tracks, ["metadata"] = new JsonObject(),
                ["effects"] = new JsonArray(), ["markers"] = new JsonArray(), ["enabled"] = true, ["source_range"] = null,
            },
        };
        Json.SaveAtomic(path, root.ToJsonString(Json.Pretty));
    }
}

/// <summary>CMX3600 EDL – the lowest common denominator (main video track + first audio track).</summary>
public sealed class EdlWriter : ITimelineFormat
{
    public string Extension => ".edl";
    public TimelineDoc Load(string path) => throw new EditException("EDL is export-only");

    public void Save(TimelineDoc d, string path)
    {
        int fps = d.IntFps;
        string Tc(long frames)
        {
            long f = frames % fps, s = frames / fps;
            return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}:{f:00}";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"TITLE: {d.Name}");
        sb.AppendLine("FCM: NON-DROP FRAME");
        sb.AppendLine();
        int n = 0;
        void Track(TTrack? t, string channel)
        {
            if (t == null) return;
            var clips = t.Clips.Where(c => c.Kind != "text").OrderBy(c => c.Start).ToList();
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                long rs = d.Frames(c.Start), re = d.Frames(c.End), si = c.Kind == "photo" ? 0 : d.Frames(c.In);
                long so = si + (long)Math.Round((re - rs) * c.Speed);
                var prev = i > 0 ? clips[i - 1] : null;
                if (channel == "V" && prev?.Transition != null && prev.End == c.Start)
                {
                    long td = d.Frames(prev.TransitionDur);
                    sb.AppendLine($"{++n:000}  AX       V     C        {Tc(si)} {Tc(si)} {Tc(rs)} {Tc(rs)}");
                    sb.AppendLine($"{n:000}  AX       V     D    {td:000} {Tc(si)} {Tc(so)} {Tc(rs)} {Tc(re)}");
                }
                else sb.AppendLine($"{++n:000}  AX       {channel,-5} C        {Tc(si)} {Tc(so)} {Tc(rs)} {Tc(re)}");
                sb.AppendLine($"* FROM CLIP NAME: {c.FileName}");
                sb.AppendLine($"* SOURCE FILE: {c.Path}");
                if (Math.Abs(c.Speed - 1) > 1e-6) sb.AppendLine($"M2   AX       {c.Speed * fps:000.0}                {Tc(si)}");
                sb.AppendLine();
            }
        }
        Track(d.Tracks.FirstOrDefault(t => t.Type == "video"), "V");
        Track(d.Tracks.FirstOrDefault(t => t.Type == "audio"), "A");
        Json.SaveAtomic(path, sb.ToString());
    }
}
