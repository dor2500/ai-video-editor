using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AIVideoEditor.AI;
using AIVideoEditor.Core;
using AIVideoEditor.Core.Timeline;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AIVideoEditor;

public partial class MainWindow : Window
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.2.0";

    const string AppOrigin = "https://aive.local";
    static readonly ConcurrentDictionary<string, byte[]> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    Agent? agent;
    bool busy;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public MainWindow()
    {
        InitializeComponent();
        Title = $"AI Video Editor {Version}";
        Loaded += async (_, _) => await InitWeb();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
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
        core.AddWebResourceRequestedFilter($"{AppOrigin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        // Keep the UI on our virtual origin; external https links open in the default browser.
        core.NavigationStarting += (_, ev) =>
        {
            if (ev.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase) || ev.Uri.StartsWith("data:") || ev.Uri == "about:blank") return;
            ev.Cancel = true;
            if (ev.Uri.StartsWith("https://")) Process.Start(new ProcessStartInfo(ev.Uri) { UseShellExecute = true });
        };
        core.NavigationCompleted += async (_, _) =>
        {
            await CheckForUpdate();
            if (Environment.GetEnvironmentVariable("AIVE_SCREENSHOT") is { Length: > 0 } shotPath)
            {
                await Task.Delay(3000);
                using var ms = new MemoryStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                await File.WriteAllBytesAsync(shotPath, ms.ToArray());
                Close();
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Uri.StartsWith("https://")) Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };
        Web.Source = new Uri($"{AppOrigin}/index.html");
    }

    // ------------------------------------------------------------------ virtual local server
    async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var def = e.GetDeferral();
        try
        {
            var env = Web.CoreWebView2.Environment;
            var uri = new Uri(e.Request.Uri);
            var route = uri.AbsolutePath.ToLowerInvariant();
            if (route is "/" or "/index.html")
                e.Response = EmbeddedResponse(env, "ui.html", "text/html; charset=utf-8");
            else if (route == "/styles.css")
                e.Response = EmbeddedResponse(env, "styles.css", "text/css; charset=utf-8");
            else if (route == "/app.js")
                e.Response = EmbeddedResponse(env, "app.js", "application/javascript; charset=utf-8");
            else if (route == "/media")
                e.Response = await MediaResponseAsync(env, uri.Query, e.Request);
            else if (route == "/thumb")
                e.Response = await ThumbResponseAsync(env, uri.Query);
            else
                e.Response = env.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain");
        }
        catch
        {
            try { e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(Stream.Null, 500, "Error", "Content-Type: text/plain"); } catch { }
        }
        finally
        {
            def.Complete();
        }
    }

    static CoreWebView2WebResourceResponse EmbeddedResponse(CoreWebView2Environment env, string name, string contentType)
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Missing embedded resource {name}");
        return env.CreateWebResourceResponse(s, 200, "OK", $"Content-Type: {contentType}\r\nCache-Control: no-store");
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) dict[Uri.UnescapeDataString(part)] = "";
            else dict[Uri.UnescapeDataString(part[..idx])] = Uri.UnescapeDataString(part[(idx + 1)..].Replace('+', ' '));
        }
        return dict;
    }

    static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" or ".aac" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    static async Task<CoreWebView2WebResourceResponse> MediaResponseAsync(
        CoreWebView2Environment env, string query, CoreWebView2WebResourceRequest req)
    {
        var q = ParseQuery(query);
        if (!q.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path) || !File.Exists(path) || Media.KindOf(path) == null)
            return env.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain");

        var mime = MimeFor(path);
        var rangeHeader = req.Headers.Contains("Range") ? req.Headers.GetHeader("Range") : null;
        var chunk = await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long total = fs.Length;
            long start = 0;
            long end = total - 1;
            bool partial = false;
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                partial = true;
                var spec = rangeHeader[6..].Split(',')[0].Trim();
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    var s0 = spec[..dash].Trim();
                    var s1 = spec[(dash + 1)..].Trim();
                    if (s0 != "" && long.TryParse(s0, out var ps)) start = Math.Clamp(ps, 0, Math.Max(0, total - 1));
                    if (s1 != "" && long.TryParse(s1, out var pe)) end = Math.Clamp(pe, start, Math.Max(0, total - 1));
                    else end = Math.Min(total - 1, start + (2 * 1024 * 1024) - 1);
                }
            }
            else if (total > 4 * 1024 * 1024)
            {
                partial = true;
                end = (2 * 1024 * 1024) - 1;
            }
            int len = total == 0 ? 0 : (int)Math.Min(4 * 1024 * 1024, end - start + 1);
            var buf = new byte[len];
            if (len > 0)
            {
                fs.Position = start;
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(buf, read, len - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != len) Array.Resize(ref buf, read);
            }
            return (buf, total, start, start + buf.Length - 1, partial);
        });

        var ms = new MemoryStream(chunk.buf, writable: false);
        if (chunk.partial && chunk.total > 0)
        {
            var headers = $"Content-Type: {mime}\r\nAccept-Ranges: bytes\r\nContent-Range: bytes {chunk.start}-{chunk.Item4}/{chunk.total}\r\nContent-Length: {chunk.buf.Length}\r\nCache-Control: private, max-age=3600";
            return env.CreateWebResourceResponse(ms, 206, "Partial Content", headers);
        }
        return env.CreateWebResourceResponse(ms, 200, "OK",
            $"Content-Type: {mime}\r\nAccept-Ranges: bytes\r\nContent-Length: {chunk.buf.Length}\r\nCache-Control: private, max-age=3600");
    }

    static async Task<CoreWebView2WebResourceResponse> ThumbResponseAsync(CoreWebView2Environment env, string query)
    {
        var q = ParseQuery(query);
        if (!q.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return env.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain");

        int width = q.TryGetValue("w", out var ws) && int.TryParse(ws, out var wi) ? Math.Clamp(wi, 64, 480) : 200;
        var cacheKey = $"{path}|{width}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (!ThumbCache.TryGetValue(cacheKey, out var bytes))
        {
            bytes = await Task.Run(async () => await ExtractThumbAsync(path, width));
            if (bytes == null || bytes.Length == 0)
                return env.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain");
            if (ThumbCache.Count > 400) ThumbCache.Clear();
            ThumbCache[cacheKey] = bytes;
        }
        return env.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK",
            $"Content-Type: image/jpeg\r\nContent-Length: {bytes.Length}\r\nCache-Control: private, max-age=86400");
    }

    static async Task<byte[]?> ExtractThumbAsync(string path, int width)
    {
        var kind = Media.KindOf(path);
        if (kind == MediaKind.Photo)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                BitmapSource frame = dec.Frames[0];
                double s = (double)width / Math.Max(frame.PixelWidth, 1);
                if (s < 1) frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(s, s));
                var enc = new JpegBitmapEncoder { QualityLevel = 78 };
                enc.Frames.Add(BitmapFrame.Create(frame));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
        if (kind == MediaKind.Video)
        {
            try
            {
                var sf = await StorageFile.GetFileFromPathAsync(path);
                using var thumb = await sf.GetThumbnailAsync(ThumbnailMode.VideosView, (uint)width, ThumbnailOptions.ResizeThumbnail);
                if (thumb == null || thumb.Size == 0) return null;
                using var rs = thumb.AsStreamForRead();
                using var ms = new MemoryStream();
                await rs.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
        return null;
    }

    // ------------------------------------------------------------------ bridge
    void Emit(string evt, object? data)
    {
        var json = JsonSerializer.Serialize(new { @event = evt, data }, Json.Relaxed);
        Dispatcher.BeginInvoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Files dropped on the window arrive as additional objects with their full paths.
        if (e.AdditionalObjects is { Count: > 0 } extra)
        {
            var paths = extra.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            Emit("dropped", paths);
            return;
        }
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

    bool updateChecked;

    /// <summary>Notifies the UI when a newer release exists on GitHub (no personal data is sent).</summary>
    async Task CheckForUpdate()
    {
        if (updateChecked) return;
        updateChecked = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AIVideoEditor/{Version}");
            var j = JsonNode.Parse(await http.GetStringAsync("https://api.github.com/repos/dor2500/ai-video-editor/releases/latest"));
            var tag = j?["tag_name"]?.ToString()?.TrimStart('v') ?? "";
            if (System.Version.TryParse(tag, out var latest) && System.Version.TryParse(Version, out var mine) && latest > mine)
                Emit("update", new { version = tag, url = j?["html_url"]?.ToString() });
        }
        catch { /* offline or rate-limited: no notice */ }
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
        IAiProvider prov = MakeProvider(st.Provider);
        providerSig = sig;
        if (agent == null)
        {
            agent = new Agent(prov, Emit);
            agent.NextProvider = tried =>
            {
                // Free services first, paid last; only services with a saved key.
                foreach (var id in new[] { "gemini", "groq", "cerebras", "openrouter", "mistral", "claude" })
                {
                    if (tried.Contains(id) || !Settings.Current.HasKeyFor(id)) continue;
                    var p = MakeProvider(id);
                    Settings.Current.Provider = id;
                    Settings.Current.Save();
                    providerSig = $"{id}|{Settings.Current.ModelFor(id)}|{Settings.Current.KeyFor(id).GetHashCode()}";
                    return (id, p);
                }
                return null;
            };
            if (st.LastProject != "" && (File.Exists(st.LastProject) || Directory.Exists(st.LastProject)))
                try { agent.Open(st.Editor, st.LastProject); } catch { }
        }
        else agent.Provider = prov;
        agent.ProviderId = st.Provider;
        return agent;
    }

    object Status()
    {
        var st = Settings.Current;
        var ag = EnsureAgent();
        return new
        {
            version = Version, provider = st.Provider, hasKey = st.HasKeyFor(st.Provider), editor = st.Editor,
            providers = Providers().Select(p => new
            {
                p.id, p.display, p.keyUrl, p.note, p.free, hasKey = st.HasKeyFor(p.id), model = st.ModelFor(p.id),
            }),
            editors = Editors.All.Select(e => new { id = e.Id, display = e.Display, installed = e.Installed, exportOnly = e.IsExportOnly, hint = e.ImportHint }),
            project = ag.Project?.Name, projectPath = ag.Project?.MainFile,
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
            case "run_tool":
            {
                if (busy) throw new EditException("Wait for the current AI task to finish");
                var p = agent?.Project ?? throw new EditException("No project is open");
                var tool = A(a, 0);
                var obj = a.Count > 1 && a[1] is JsonObject jo ? jo : new JsonObject();
                var label = A(a, 2, tool);
                if (!p.SupportedTools.Contains(tool))
                    throw new EditException($"'{tool}' is not supported in {Editors.Get(st.Editor).Display}");
                bool mut = ToolDefs.Mutating.Contains(tool);
                if (mut) p.Snapshot(label);
                var res = p.Run(tool, new Args(obj));
                if (mut) p.Save();
                return new { result = res, overview = p.Overview(), afterSave = p.AfterSaveMessage };
            }
            case "probe_media":
            {
                var arr = a.Count > 0 && a[0] is JsonArray ja ? ja.Select(x => x?.ToString() ?? "").Where(s => s != "").ToList() : [];
                var items = new List<object>();
                foreach (var raw in arr)
                {
                    var expanded = Directory.Exists(Paths.Expand(raw))
                        ? Directory.GetFiles(Paths.Expand(raw)).Where(f => Media.KindOf(f) != null).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                        : [Paths.Expand(raw)];
                    foreach (var f in expanded)
                    {
                        try
                        {
                            var pr = Media.Probe(f);
                            items.Add(new
                            {
                                path = Path.GetFullPath(f),
                                name = Path.GetFileName(f),
                                kind = pr.Kind.ToString().ToLowerInvariant(),
                                duration_s = pr.Kind == MediaKind.Photo ? 3.0 : T.Sec(pr.DurationUs),
                                width = pr.Width,
                                height = pr.Height,
                            });
                        }
                        catch { }
                    }
                }
                return items;
            }
            case "search_catalog":
            {
                var cat = A(a, 0, "transition");
                var q = A(a, 1, "");
                var p = agent?.Project;
                if (p != null && p.SupportedTools.Contains("search_catalog"))
                    return p.Run("search_catalog", new Args(new JsonObject { ["category"] = cat, ["query"] = q, ["limit"] = 60 }));
                if (cat == "transition")
                    return GenericCatalog.Transitions.Select(x => new { name = x.Name, description = x.Desc });
                if (cat is "video_in" or "text_in")
                    return new[] { new { name = GenericCatalog.Animations[0].Name, description = GenericCatalog.Animations[0].Desc } };
                if (cat is "video_out" or "text_out")
                    return new[] { new { name = GenericCatalog.Animations[1].Name, description = GenericCatalog.Animations[1].Desc } };
                return Array.Empty<object>();
            }
            case "export_project":
            {
                var p = agent?.Project ?? throw new EditException("No project is open");
                p.Save();
                var ed = Editors.Get(st.Editor);
                var files = p is TimelineProject tp && tp.LastExports.Count > 0 ? tp.LastExports : new List<string> { p.MainFile };
                return new
                {
                    editor = ed.Display,
                    exportOnly = ed.IsExportOnly,
                    hint = ed.ImportHint,
                    mainFile = p.MainFile,
                    files = files.Select(f => new { name = Path.GetFileName(f), path = f }),
                    message = p.AfterSaveMessage ?? $"Project saved to {p.MainFile}",
                };
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
                return new { label, overview = agent.Project.Overview() };
            }
            case "redo":
            {
                if (agent?.Project == null) throw new EditException("No project open");
                var label = agent.Project.Redo();
                return new { label, overview = agent.Project.Overview() };
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

    /// <summary>Builds the client for a service. An empty model means "pick the best available one at first use".</summary>
    static IAiProvider MakeProvider(string id)
    {
        var st = Settings.Current;
        var model = st.ModelFor(id);
        var key = st.KeyFor(id);
        return id switch
        {
            "gemini" => new GeminiProvider(key, model),
            "claude" => new ClaudeProvider(key, model is { Length: > 0 } ? model : "claude-opus-5-5"),
            _ => Services.Get(id) is { } svc ? new OpenAiCompatProvider(svc, key, model)
                : throw new EditException("Unknown AI provider " + id),
        };
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
            return "Invalid API key. Open Settings and enter a valid key.";
        if (ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
            return "Anthropic account credit balance is exhausted. Switch to a free provider in Settings (Gemini Flash, Groq, OpenRouter, Cerebras, Mistral).";
        if (ex is AiException { Status: 429 } && (ex.Message.Contains("limit: 0") || ex.Message.Contains("free_tier", StringComparison.OrdinalIgnoreCase)))
            return "The selected model is not included in the free tier. Pick another model in Settings (e.g., Gemini Flash).";
        if (n.Contains("RateLimit") || ex is AiException { Status: 429 })
            return "Rate limit reached for this service. Wait a moment or switch to another provider in Settings.";
        if (ex is HttpRequestException || n.Contains("Connection"))
            return "Unable to reach the AI service. Check your internet connection.";
        return ex.Message;
    }
}
