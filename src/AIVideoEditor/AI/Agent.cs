using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using AIVideoEditor.Core;

namespace AIVideoEditor.AI;

public sealed record ToolCall(string Id, string Name, JsonObject Args);

public sealed class ToolOutput
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public required string Text { get; init; }
    public bool IsError { get; init; }
    public List<(string Label, byte[] Jpeg)> Images { get; init; } = [];
}

public sealed record AiTurn(List<string> Texts, List<ToolCall> Calls, string Stop);

/// <summary>A chat model with tool use. Each provider keeps its own wire-format history.</summary>
public interface IAiProvider
{
    string Display { get; }
    void Reset();
    void AddUser(string text);
    void AddToolResults(List<ToolOutput> results);
    Task<AiTurn> NextAsync(string system, List<ToolDef> tools, CancellationToken ct);
}

/// <summary>Runs the chat ↔ tools loop against whatever editor project is open.</summary>
public sealed class Agent(IAiProvider provider, Action<string, object?> emit)
{
    public EditorProject? Project { get; private set; }
    public string EditorId { get; set; } = Settings.Current.Editor;
    public IAiProvider Provider { get; set; } = provider;
    CancellationTokenSource? cts;
    bool snapped;
    string request = "";

    const string SystemBase = """
        You are a video-editing assistant inside the "AI Video Editor" app. You edit the user's project in their video editor
        directly by changing its project file. The user chats with you (often in Hebrew); reply in the user's language, short and friendly.

        Timeline conventions (all editors):
        - Times are in seconds. Segment ids are the 8-character ids from get_project_overview (prefixes are fine).
        - Tracks: "video#0" is the MAIN track (magnetic: clips sit back-to-back; inserting/deleting ripples). "video#1"... are overlays /
          picture-in-picture. There are also audio#N, text#N and (CapCut) filter#N / effect#N tracks.
        - Position x,y: units of HALF the canvas. (0,0)=center, x=1 right edge, x=-1 left edge, y=1 TOP, y=-1 BOTTOM.
          Text near the bottom is y≈-0.7, a title near the top y≈0.6. scale 1.0 = fit canvas. opacity 0..1. rotation in degrees.
        - Volume 1.0 = 100%, 0 = mute, up to ~2.0 louder.
        - Effects/transitions/animations/filters/fonts are referenced by NAME from search_catalog. Always search before using one.
          In CapCut, prefer items with used_before=true and avoid vip=true unless the user asks (they need CapCut Pro).

        Working rules:
        - Start a request with get_project_overview (unless you just did and nothing changed).
        - When the user names a folder of photos/videos, call list_folder_media first. To pick or order photos by content,
          use look_at_images on a few candidates.
        - Do the whole job in as few tool calls as reasonable (tools accept lists of ids), then summarize briefly what changed.
        - If a request is ambiguous in a way that matters (which song? which folder?), ask one short question instead of guessing.
        - If something is not possible in the current editor, say so plainly and suggest the closest alternative.
        - Each user request is one undo step; the user can press Undo or ask you (undo_last_request).
        """;

    public void Open(string editorId, string path)
    {
        Project = Editors.Open(editorId, path);
        EditorId = editorId;
        Settings.Current.Editor = editorId;
        Settings.Current.LastProject = path;
        Settings.Current.Save();
        Provider.Reset();
        emit("project", new { name = Project.Name, path, editor = editorId });
    }

    void SwitchKeepChat(string path)
    {
        Project = Editors.Open(EditorId, path);
        Settings.Current.LastProject = path;
        Settings.Current.Save();
        snapped = false;
        emit("project", new { name = Project.Name, path, editor = EditorId });
    }

    public void Stop() => cts?.Cancel();

    /// <summary>Switch editor: the open project belongs to the old one, so close it.</summary>
    public void SetEditor(string editorId)
    {
        if (editorId == EditorId) return;
        EditorId = editorId;
        Project = null;
        Provider.Reset();
    }

    public void NoteUndo(string label)
    {
        Provider.AddUser($"[The user pressed UNDO in the app; the request '{label}' was reverted. Re-read the project before editing.]");
    }

    public async Task SendAsync(string text)
    {
        request = text;
        snapped = false;
        cts = new CancellationTokenSource();
        var ed = Editors.Get(EditorId);
        var ctx = Project != null ? $"[Editor: {ed.Display} · Project: {Project.Name}]\n" : $"[Editor: {ed.Display} · no project open yet]\n";
        Provider.AddUser(ctx + text);
        var system = SystemBase + "\n\nCurrent editor: " + ed.Display + ". " + (Project?.Notes ?? "");
        for (int step = 0; step < 40; step++)
        {
            if (cts.IsCancellationRequested) { emit("assistant", "⏹ נעצר."); return; }
            var tools = ToolDefs.For(Project?.SupportedTools);
            AiTurn turn;
            try { turn = await Provider.NextAsync(system, tools, cts.Token); }
            catch (OperationCanceledException) { emit("assistant", "⏹ נעצר."); return; }
            foreach (var t in turn.Texts.Where(t => !string.IsNullOrWhiteSpace(t))) emit("assistant", t);
            if (turn.Stop == "refusal") { emit("error", "The model declined this request."); return; }
            if (turn.Stop == "max_tokens" && turn.Calls.Count == 0) { emit("error", "The reply was cut off (max tokens)."); return; }
            if (turn.Calls.Count == 0) return;
            var results = new List<ToolOutput>();
            bool changed = false;
            foreach (var call in turn.Calls)
            {
                emit("tool", new { name = call.Name, input = call.Args });
                try
                {
                    var (output, images, mut) = Run(call.Name, call.Args);
                    if (mut) { Project!.Save(); changed = true; }
                    if (call.Name == "undo_last_request") changed = true;
                    results.Add(new ToolOutput { CallId = call.Id, Name = call.Name, Text = output, Images = images });
                }
                catch (Exception e)
                {
                    var msg = e is EditException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                    results.Add(new ToolOutput { CallId = call.Id, Name = call.Name, Text = "Error: " + msg, IsError = true });
                }
            }
            if (changed)
            {
                emit("changed", Project?.AfterSaveMessage);
            }
            Provider.AddToolResults(results);
        }
        emit("error", "Too many steps — stopped.");
    }

    (string text, List<(string, byte[])> images, bool mutated) Run(string name, JsonObject args)
    {
        var a = new Args(args);
        string J(JsonNode? n) => n?.ToJsonString(Json.Relaxed) ?? "null";
        switch (name)
        {
            case "list_projects":
                return (J(Json.From(Editors.Projects(EditorId).Select(p => new { p.Name, modified = p.Modified.ToString("g") }))), [], false);
            case "switch_project":
            {
                var n = a.Req("name");
                var p = Editors.Projects(EditorId).FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                        ?? Editors.Projects(EditorId).FirstOrDefault(x => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                        ?? throw new EditException($"No project named '{n}'");
                SwitchKeepChat(p.Path);
                return (J(Project!.Overview()), [], false);
            }
            case "create_project":
            {
                var path = Editors.Create(EditorId, a.Req("name"), a.S("ratio", "16:9"));
                SwitchKeepChat(path);
                return (J(new JsonObject { ["created"] = Project!.Name }), [], false);
            }
            case "list_folder_media":
                return (J(Media.ListFolder(a.Req("folder"), a.B("recursive", false))), [], false);
            case "look_at_images":
            {
                var imgs = new List<(string, byte[])>();
                foreach (var p in a.L("paths").Take(8))
                    if (Thumb(Paths.Expand(p)) is { } jpg) imgs.Add((Path.GetFileName(p), jpg));
                return (imgs.Count > 0 ? $"{imgs.Count} images attached" : "Could not open any of those images", imgs, false);
            }
        }
        if (Project == null)
            throw new EditException("No project is open. Ask the user to pick one, or use list_projects / switch_project / create_project.");
        if (name == "get_project_overview") return (J(Project.Overview()), [], false);
        if (name == "undo_last_request") return (J(new JsonObject { ["undone"] = Project.Undo() }), [], false);
        bool mut = ToolDefs.Mutating.Contains(name);
        if (mut && !snapped)
        {
            Project.Snapshot(request.Length > 120 ? request[..120] : request);
            snapped = true;
        }
        var res = Project.Run(name, a);
        return (J(res), [], mut);
    }

    static byte[]? Thumb(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = dec.Frames[0];
            double s = 512.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (s < 1) frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(s, s));
            var enc = new JpegBitmapEncoder { QualityLevel = 75 };
            enc.Frames.Add(BitmapFrame.Create(frame));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
