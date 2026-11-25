using System.Text.Json.Nodes;

namespace AIVideoEditor.AI;

public sealed record ToolDef(string Name, string Description, JsonObject Properties, string[] Required);

public static class ToolDefs
{
    static JsonObject S(string? desc = null, string[]? @enum = null)
    {
        var o = new JsonObject { ["type"] = "string" };
        if (desc != null) o["description"] = desc;
        if (@enum != null) o["enum"] = new JsonArray(@enum.Select(e => (JsonNode)e).ToArray());
        return o;
    }
    static JsonObject N(string? desc = null) => desc == null ? new() { ["type"] = "number" } : new() { ["type"] = "number", ["description"] = desc };
    static JsonObject B(string? desc = null) => desc == null ? new() { ["type"] = "boolean" } : new() { ["type"] = "boolean", ["description"] = desc };
    static JsonObject L(string? desc = null)
    {
        var o = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        if (desc != null) o["description"] = desc;
        return o;
    }
    static JsonObject P(params (string k, JsonObject v)[] props)
    {
        var o = new JsonObject();
        foreach (var (k, v) in props) o[k] = v;
        return o;
    }

    static readonly string[] Ratios = ["16:9", "9:16", "1:1", "4:3", "3:4", "4:5", "21:9", "2:1"];

    /// <summary>Tools that work regardless of editor (handled by the agent).</summary>
    public static readonly List<ToolDef> Global =
    [
        new("get_project_overview", "Return tracks and segments of the open project (ids, times, files, effects).", P(), []),
        new("list_projects", "List the projects of the current editor on this PC.", P(), []),
        new("switch_project", "Open another existing project of the current editor by its name.", P(("name", S())), ["name"]),
        new("create_project", "Create a new empty project in the current editor and switch to it.",
            P(("name", S()), ("ratio", S(null, Ratios))), ["name"]),
        new("list_folder_media", "List photos, videos and audio files in a folder on disk.",
            P(("folder", S()), ("recursive", B())), ["folder"]),
        new("look_at_images", "See up to 8 images (thumbnails) so you can choose or order them by content.",
            P(("paths", L())), ["paths"]),
        new("undo_last_request", "Restore the project to how it was before the previous request.", P(), []),
    ];

    /// <summary>Project tools; each editor exposes the subset it supports.</summary>
    public static readonly List<ToolDef> Project =
    [
        new("add_media", "Add photos/videos (files or whole folders). target='main' appends to the main track (or inserts at `start`, rippling later clips); 'overlay' = overlay track at half size from `start`; 'new_track' = overlay track at full size.",
            P(("paths", L()), ("target", S(null, ["main", "overlay", "new_track"])), ("start", N()),
              ("photo_duration", N("seconds per photo, default 3")), ("max_video_duration", N()), ("sort_by_name", B())), ["paths"]),
        new("add_audio", "Add music / sound. fit_to_video=true spans the whole video (loops the song if it is shorter).",
            P(("path", S()), ("start", N()), ("duration", N()), ("trim_start", N("skip this many seconds of the song")),
              ("fit_to_video", B()), ("loop", B()), ("volume", N()), ("fade_in", N()), ("fade_out", N()), ("new_track", B())), ["path"]),
        new("add_text", "Add a text / title / caption.",
            P(("text", S()), ("start", N()), ("duration", N()), ("font_size", N("default 8; titles 12-20")), ("color", S("#RRGGBB")),
              ("x", N()), ("y", N()), ("bold", B()), ("italic", B()), ("align", S(null, ["left", "center", "right"])),
              ("stroke_color", S()), ("stroke_width", N()), ("background_color", S()), ("background_alpha", N()), ("shadow", B()),
              ("font", S("font name (CapCut: from search_catalog('font'); other editors: a system font name)"))),
            ["text", "start", "duration"]),
        new("edit_segments", "Change properties of one or more segments; only given fields change. Duration changes on the main track ripple later clips.",
            P(("ids", L()), ("start", N()), ("duration", N()), ("x", N()), ("y", N()), ("scale", N()), ("rotation", N()), ("opacity", N()),
              ("volume", N()), ("speed", N()), ("flip_horizontal", B()), ("flip_vertical", B()), ("visible", B()),
              ("text", S()), ("font_size", N()), ("color", S()), ("bold", B())), ["ids"]),
        new("move_segment", "Move a segment in time and/or to another track ('video#1', 'text#0', 'audio#new'...).",
            P(("id", S()), ("start", N()), ("to_track", S())), ["id"]),
        new("delete_segments", "Delete segments. On the main track later clips close the gap unless ripple=false.",
            P(("ids", L()), ("ripple", B())), ["ids"]),
        new("split_segment", "Cut a segment in two at an absolute time (seconds).", P(("id", S()), ("at", N())), ["id", "at"]),
        new("reorder_main_track", "Reorder clips of the main track; listed ids go first in that order, others follow; gaps removed.",
            P(("order", L())), ["order"]),
        new("close_gaps", "Remove empty gaps on a track (default video#0).", P(("track", S())), []),
        new("set_track_mute", "Mute/unmute a whole track, e.g. 'video#0' to silence the original clip sound.",
            P(("track", S()), ("muted", B())), ["track", "muted"]),
        new("set_track_hidden", "Hide/show a whole visual track, e.g. 'video#1'.",
            P(("track", S()), ("hidden", B())), ["track", "hidden"]),
        new("set_canvas", "Change the aspect ratio of the project.", P(("ratio", S(null, Ratios))), ["ratio"]),
        new("search_catalog", "Search effects by name. Categories: transition, video_in, video_out, video_combo, text_in, text_out, text_loop, filter, effect, font. Empty query lists the first items.",
            P(("category", S()), ("query", S()), ("limit", N())), ["category"]),
        new("set_transitions", "Set the transition after each given segment (between it and the next clip on its track). all_main=true applies to every main-track clip except the last. name='none' removes.",
            P(("ids", L()), ("all_main", B()), ("name", S()), ("duration", N())), []),
        new("set_animations", "Set in/out/group animation on segments (video or text; text uses in/out/loop). name='none' removes that kind.",
            P(("ids", L()), ("all_main", B()), ("kind", S(null, ["in", "out", "group", "loop"])), ("name", S()), ("duration", N())), ["kind"]),
        new("add_motion", "Ken-Burns keyframe motion over the whole clip: zoom_in, zoom_out, pan_left, pan_right, pan_up, pan_down, fade_in_out, none; 'alternate' cycles zoom_in/pan_left/zoom_out/pan_right.",
            P(("ids", L()), ("all_main_photos", B()), ("preset", S()), ("strength", N("0.05-0.4, default 0.15"))), ["preset"]),
        new("add_filter_or_effect", "Add a filter (color look) or a video effect over a time range as its own track.",
            P(("category", S(null, ["filter", "effect"])), ("name", S()), ("start", N()), ("duration", N()), ("intensity", N())),
            ["category", "name", "start", "duration"]),
        new("set_audio_fade", "Set fade in/out (seconds) on audio or video segments' sound.",
            P(("ids", L()), ("fade_in", N()), ("fade_out", N())), ["ids"]),
    ];

    public static List<ToolDef> For(IReadOnlySet<string>? projectTools) =>
        Global.Concat(Project.Where(t => projectTools?.Contains(t.Name) ?? false)).ToList();

    public static readonly HashSet<string> Mutating =
    [
        "add_media", "add_audio", "add_text", "edit_segments", "move_segment", "delete_segments", "split_segment",
        "reorder_main_track", "close_gaps", "set_track_mute", "set_track_hidden", "set_canvas", "set_transitions", "set_animations",
        "add_motion", "add_filter_or_effect", "set_audio_fade",
    ];
}
