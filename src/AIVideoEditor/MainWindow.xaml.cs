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
        var model = st.ModelFor(st.Provider);
        var key = st.KeyFor(st.Provider);
        var sig = $"{st.Provider}|{model}|{key.GetHashCode()}";
        if (agent != null && sig == providerSig) return agent;
        IAiProvider prov = st.Provider switch
        {
            "gemini" => new GeminiProvider(key, model is { Length: > 0 } ? model : "gemini-2.5-flash"),
            "claude" => new ClaudeProvider(key, model is { Length: > 0 } ? model : "claude-opus-5-5"),
            _ => Services.Get(st.Provider) is { } svc
                ? new OpenAiCompatProvider(svc, key, model is { Length: > 0 } ? model : svc.PreferredModels[0])
                : throw new EditException("Unknown AI provider " + st.Provider),
        };
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
            version = Version, provider = st.Provider, hasKey = st.HasKeyFor(st.Provider), editor = st.Editor,
            providers = Providers().Select(p => new
            {
                p.id, p.display, p.keyUrl, p.note, p.free, hasKey = st.HasKeyFor(p.id), model = st.ModelFor(p.id),
            }),
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
                var p = o["provider"]?.ToString() is { Length: > 0 } pp ? pp : st.Provider;
                st.Provider = p;
                if (o["key"]?.ToString() is { Length: > 0 } key) st.SetKey(p, key);
                if (o["model"]?.ToString() is { } model) st.SetModel(p, model);
                if (st.ModelFor(p) == "" && st.HasKeyFor(p) && p != "claude")
                    try { st.SetModel(p, (await ListModels(p, st.KeyFor(p))).FirstOrDefault() ?? ""); } catch { }
                st.Save();
                agent?.NewChat();
                if (agent != null) EnsureAgent();
                return Status();
            }
            case "list_models":
            {
                var p = A(a, 0);
                var key = A(a, 1) is { Length: > 0 } k ? k : st.KeyFor(p);
                if (key == "") throw new EditException("Enter an API key first");
                return await ListModels(p, key);
            }
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

    static readonly (string id, string display, string keyUrl, string note, bool free)[] Paid =
    [
        ("claude", "Claude (Anthropic)", "https://console.anthropic.com/settings/keys", "Paid (pay per use). Best at editing tasks.", false),
        ("gemini", "Gemini (Google)", "https://aistudio.google.com/apikey",
            "Free tier for Flash models (no card). Pro models need billing. Free-tier prompts may be used by Google.", true),
    ];

    static IEnumerable<(string id, string display, string keyUrl, string note, bool free)> Providers() =>
        Paid.Concat(Services.Free.Select(s => (s.Id, s.Display, s.KeyUrl, s.FreeNote, true)));

    static async Task<List<string>> ListModels(string provider, string key) => provider switch
    {
        "gemini" => await GeminiProvider.ListModels(key),
        "claude" => ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2"],
        _ => await OpenAiCompatProvider.ListModels(Services.Get(provider) ?? throw new EditException("Unknown provider"), key),
    };

    object Projects() => Editors.Projects(Settings.Current.Editor)
        .Select(p => new { name = p.Name, path = p.Path, duration = p.DurationS, modified = p.Modified.ToString("g") });

    static string Friendly(Exception ex)
    {
        var n = ex.GetType().Name;
        if (n.Contains("Unauthorized") || n.Contains("Authentication") || ex is AiException { Status: 401 or 403 })
            return "מפתח ה-API לא תקין. פתח הגדרות והזן מפתח חדש.";
        if (ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
            return "נגמר הקרדיט בחשבון Anthropic. אפשר לעבור בהגדרות לשירות חינמי (Gemini Flash, Groq, OpenRouter, Cerebras, Mistral).";
        if (ex is AiException { Status: 429 } && (ex.Message.Contains("limit: 0") || ex.Message.Contains("free_tier", StringComparison.OrdinalIgnoreCase)))
            return "המודל שנבחר לא כלול במסלול החינמי. בהגדרות בחר מודל אחר (ב-Gemini: מודל flash).";
        if (n.Contains("RateLimit") || ex is AiException { Status: 429 })
            return "הגעת למגבלת השימוש של השירות. חכה דקה (או עד מחר במגבלה יומית), או עבור בהגדרות לשירות חינמי אחר.";
        if (ex is HttpRequestException || n.Contains("Connection"))
            return "אין חיבור לאינטרנט / לשרת ה-AI.";
        return ex.Message;
    }
}
