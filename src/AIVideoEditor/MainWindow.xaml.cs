using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using AIVideoEditor.AI;
using AIVideoEditor.Core;
using Microsoft.Web.WebView2.Core;

namespace AIVideoEditor;

public partial class MainWindow : Window
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0.0";

    Agent? agent;
    bool busy;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"AI Video Editor {Version}";
        Loaded += async (_, _) => await InitWeb();
    }

    async Task InitWeb()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.LocalAppData, "AIVideoEditor", "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception)
        {
            Web.Visibility = Visibility.Collapsed;
            Fallback.Visibility = Visibility.Visible;
            return;
        }
        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("AIVE_DEBUG") == "1";
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = core.Settings.AreDevToolsEnabled;
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Uri.StartsWith("https://")) Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html")!;
        using var r = new StreamReader(s);
        Web.NavigateToString(r.ReadToEnd());
    }

    // ------------------------------------------------------------------ bridge
    void Emit(string evt, object? data)
    {
        var json = JsonSerializer.Serialize(new { @event = evt, data }, Json.Relaxed);
        Dispatcher.BeginInvoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        var id = msg?["id"]?.GetValue<int>() ?? 0;
        var method = msg?["method"]?.ToString() ?? "";
        var args = msg?["args"] as JsonArray ?? [];
        object? result;
        string? error = null;
        try { result = await Call(method, args); }
        catch (Exception ex) { result = null; error = ex.Message; }
        var json = JsonSerializer.Serialize(new { id, result, error }, Json.Relaxed);
        Web.CoreWebView2?.PostWebMessageAsJson(json);
    }

    static string A(JsonArray a, int i, string def = "") => i < a.Count ? a[i]?.ToString() ?? def : def;

    string providerSig = "";

    Agent EnsureAgent()
    {
        var st = Settings.Current;
        var gm = st.GeminiModel is { Length: > 0 } g ? g : "gemini-2.5-pro";
        var cm = st.ClaudeModel is { Length: > 0 } c ? c : "claude-opus-5-5";
        var sig = st.Provider == "gemini" ? $"g|{gm}|{st.GeminiKey.GetHashCode()}" : $"c|{cm}|{st.ClaudeKey.GetHashCode()}";
        if (agent != null && sig == providerSig) return agent;
        IAiProvider prov = st.Provider == "gemini" ? new GeminiProvider(st.GeminiKey, gm) : new ClaudeProvider(st.ClaudeKey, cm);
        providerSig = sig;
        if (agent == null)
        {
            agent = new Agent(prov, Emit);
            if (st.LastProject != "" && (File.Exists(st.LastProject) || Directory.Exists(st.LastProject)))
                try { agent.Open(st.Editor, st.LastProject); } catch { }
        }
        else agent.Provider = prov;
        return agent;
    }

    object Status()
    {
        var st = Settings.Current;
        var ag = st.HasKeyFor(st.Provider) ? EnsureAgent() : null;
        return new
        {
            version = Version, provider = st.Provider, hasClaude = st.ClaudeKey != "", hasGemini = st.GeminiKey != "",
            hasKey = st.HasKeyFor(st.Provider), claudeModel = st.ClaudeModel, geminiModel = st.GeminiModel, editor = st.Editor,
            editors = Editors.All.Select(e => new { id = e.Id, display = e.Display, installed = e.Installed, exportOnly = e.IsExportOnly, hint = e.ImportHint }),
            project = ag?.Project?.Name, projectPath = ag?.Project?.MainFile,
        };
    }

    async Task<object?> Call(string method, JsonArray a)
    {
        var st = Settings.Current;
        switch (method)
        {
            case "init": return Status();
            case "save_settings":
            {
                var o = a[0]!.AsObject();
                if (o["provider"]?.ToString() is { Length: > 0 } p) st.Provider = p;
                if (o["claudeKey"]?.ToString() is { Length: > 0 } ck) st.ClaudeKey = ck;
                if (o["geminiKey"]?.ToString() is { Length: > 0 } gk) st.GeminiKey = gk;
                if (o["claudeModel"]?.ToString() is { Length: > 0 } cm) st.ClaudeModel = cm;
                if (o["geminiModel"]?.ToString() is { } gmod) st.GeminiModel = gmod;
                if (st.Provider == "gemini" && st.GeminiModel == "" && st.GeminiKey != "")
                    try { st.GeminiModel = (await GeminiProvider.ListModels(st.GeminiKey)).FirstOrDefault() ?? ""; } catch { }
                st.Save();
                agent?.NewChat();
                if (agent != null) EnsureAgent();
                return Status();
            }
            case "gemini_models":
                return await GeminiProvider.ListModels(A(a, 0) is { Length: > 0 } k ? k : st.GeminiKey);
            case "set_editor":
            {
                if (busy) throw new EditException("Wait for the current request to finish");
                st.Editor = A(a, 0);
                st.LastProject = "";
                st.Save();
                agent?.SetEditor(st.Editor);
                return Projects();
            }
            case "projects": return Projects();
            case "open_project":
            {
                var ag = EnsureAgent();
                ag.Open(st.Editor, A(a, 0));
                return ag.Project!.Overview();
            }
            case "open_file":
            {
                var ed = Editors.Get(st.Editor);
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = ed.Id == "capcut" ? "CapCut draft|draft_content.json"
                        : ed.IsExportOnly ? "AI Video Editor timeline|*.aive.json"
                        : $"{ed.Display} project|{string.Join(";", ed.ProjectExts.Select(x => "*" + x))}",
                };
                if (dlg.ShowDialog(this) != true) return null;
                var path = ed.Id == "capcut" ? Path.GetDirectoryName(dlg.FileName)! : dlg.FileName;
                var ag = EnsureAgent();
                ag.Open(st.Editor, path);
                return ag.Project!.Overview();
            }
            case "new_project":
            {
                var path = Editors.Create(st.Editor, A(a, 0), A(a, 1, "16:9"));
                var ag = EnsureAgent();
                ag.Open(st.Editor, path);
                return ag.Project!.Overview();
            }
            case "overview":
            {
                var p = agent?.Project;
                if (p == null) return null;
                try { p.Reload(); } catch { }
                return p.Overview();
            }
            case "editor_status":
            {
                var ed = Editors.Get(st.Editor);
                return new { running = ed.Running, installed = ed.Installed, exportOnly = ed.IsExportOnly };
            }
            case "close_editor":
            {
                var ed = Editors.Get(st.Editor);
                return new { closed = ed.Process == null || await Task.Run(() => Proc.Close(ed.Process)) };
            }
            case "open_editor":
            {
                var ed = Editors.Get(st.Editor);
                Editors.Launch(ed, agent?.Project?.MainFile);
                return true;
            }
            case "reveal":
            {
                var p = agent?.Project?.MainFile;
                if (p != null) Process.Start("explorer.exe", Directory.Exists(p) ? $"\"{p}\"" : $"/select,\"{p}\"");
                return true;
            }
            case "undo":
            {
                if (agent?.Project == null) throw new EditException("No project open");
                var label = agent.Project.Undo();
                agent.NoteUndo(label);
                return new { label };
            }
            case "new_chat": agent?.NewChat(); return true;
            case "stop": agent?.Stop(); return true;
            case "pick_folder":
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog();
                return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
            }
            case "pick_files":
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Multiselect = true,
                    Filter = "Media|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.bmp;*.gif;*.mp4;*.mov;*.avi;*.mkv;*.wmv;*.m4v;*.webm;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg|All files|*.*",
                };
                return dlg.ShowDialog(this) == true ? dlg.FileNames : Array.Empty<string>();
            }
            case "open_url":
            {
                var u = A(a, 0);
                if (u.StartsWith("https://")) Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                return true;
            }
            case "send":
            {
                if (busy) return new { ok = false, error = "busy" };
                if (!st.HasKeyFor(st.Provider)) return new { ok = false, error = "nokey" };
                busy = true;
                var ag = EnsureAgent();
                var text = A(a, 0);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        try { ag.Project?.Reload(); } catch { }
                        await ag.SendAsync(text);
                    }
                    catch (Exception ex) { Emit("error", Friendly(ex)); }
                    finally
                    {
                        busy = false;
                        Emit("done", null);
                    }
                });
                return new { ok = true };
            }
        }
        throw new InvalidOperationException("Unknown method " + method);
    }

    object Projects() => Editors.Projects(Settings.Current.Editor)
        .Select(p => new { name = p.Name, path = p.Path, duration = p.DurationS, modified = p.Modified.ToString("g") });

    static string Friendly(Exception ex)
    {
        var n = ex.GetType().Name;
        if (n.Contains("Unauthorized") || n.Contains("Authentication") || ex is AiException { Status: 401 or 403 })
            return "מפתח ה-API לא תקין. פתח הגדרות והזן מפתח חדש.";
        if (n.Contains("RateLimit") || ex is AiException { Status: 429 })
            return "הגעת למגבלת שימוש של ה-API, נסה שוב בעוד רגע.";
        if (ex is HttpRequestException || n.Contains("Connection"))
            return "אין חיבור לאינטרנט / לשרת ה-AI.";
        return ex.Message;
    }
}
