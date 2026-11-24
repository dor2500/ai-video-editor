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
    IAiProvider provider = provider;
    public IAiProvider Provider
    {
        get => provider;
        set { provider = value; lastContextKey = ""; }
    }

    /// <summary>Forget the conversation (the project stays open).</summary>
    public void NewChat()
    {
        provider.Reset();
        lastContextKey = "";
    }
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
        NewChat();
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
        NewChat();
    }

    public void NoteUndo(string label)
    {
        Provider.AddUser($"[The user pressed UNDO in the app; the request '{label}' was reverted. Re-read the project before editing.]");
    }

    // The system prompt and the tool list must stay byte-identical for the whole conversation:
    // Claude's thinking blocks are bound to the prompt they were produced with, and caching needs a stable prefix.
    // Everything that changes (editor, project, editor notes) goes into the conversation itself.
    static readonly List<ToolDef> AllTools = ToolDefs.For(ToolDefs.Project.Select(t => t.Name).ToHashSet());
    string lastContextKey = "";

    string Context()
    {
        var ed = Editors.Get(EditorId);
        var key = $"{EditorId}|{Project?.MainFile}";
        var head = Project != null ? $"[Editor: {ed.Display} · Project: {Project.Name}]" : $"[Editor: {ed.Display} · no project open yet]";
        if (key == lastContextKey) return head + "\n";
        lastContextKey = key;
        var notes = Project?.Notes is { Length: > 0 } n ? n : ed.IsExportOnly ? $"{ed.Display} is edited through exported import files." : "";
        var unsupported = Project == null ? "" :
            string.Join(", ", ToolDefs.Project.Select(t => t.Name).Where(t => !Project.SupportedTools.Contains(t)));
        return head + (notes != "" ? "\n[Editor notes: " + notes + "]" : "") +
               (unsupported != "" ? $"\n[Not available in this editor: {unsupported}]" : "") + "\n";
    }

    public async Task SendAsync(string text)
    {
        request = text;
        snapped = false;
        cts = new CancellationTokenSource();
        var userText = Context() + text;
        Provider.AddUser(userText);
        bool retried = false;
        for (int step = 0; step < 40; step++)
        {
            if (cts.IsCancellationRequested) { emit("assistant", "⏹ נעצר."); return; }
            AiTurn turn;
            try { turn = await Provider.NextAsync(SystemBase, AllTools, cts.Token); }
            catch (OperationCanceledException) { emit("assistant", "⏹ נעצר."); return; }
            catch (Exception e) when (!retried && IsHistoryMismatch(e))
            {
                // The stored conversation no longer matches what the API expects (e.g. an older app version changed
                // the prompt mid-chat). Start a fresh conversation with this request instead of failing.
                retried = true;
                Provider.Reset();
                lastContextKey = "";
                Provider.AddUser(Context() + text);
                emit("info", "השיחה התחילה מחדש בגלל שגיאה טכנית — ממשיך בבקשה.");
                step--;
                continue;
            }
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

    static bool IsHistoryMismatch(Exception e)
    {
        var m = e.Message;
        return m.Contains("signature", StringComparison.OrdinalIgnoreCase) && m.Contains("thinking", StringComparison.OrdinalIgnoreCase)
               || m.Contains("bound to a different conversation", StringComparison.OrdinalIgnoreCase)
               || m.Contains("thought_signature", StringComparison.OrdinalIgnoreCase)
               || m.Contains("thought signature", StringComparison.OrdinalIgnoreCase);
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
                return (J(new JsonObject { ["opened"] = Project!.Overview(), ["context"] = Context() }), [], false);
            }
            case "create_project":
            {
                var path = Editors.Create(EditorId, a.Req("name"), a.S("ratio", "16:9"));
                SwitchKeepChat(path);
                return (J(new JsonObject { ["created"] = Project!.Name, ["context"] = Context() }), [], false);
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
        if (ToolDefs.Project.Any(t => t.Name == name) && !Project.SupportedTools.Contains(name))
            throw new EditException($"'{name}' is not available in {Editors.Get(EditorId).Display}.");
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
