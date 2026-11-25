// Self-test for the editing engines and format writers. No AI calls.
// Usage: SelfTest [--media <folder with photos/videos>] [--audio <file>] [--capcut <draft folder to COPY and test>]
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIVideoEditor.Core;
using AIVideoEditor.Core.CapCut;
using AIVideoEditor.Core.Timeline;

string? media = null, audio = null, capcut = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--media") media = args[i + 1];
    if (args[i] == "--audio") audio = args[i + 1];
    if (args[i] == "--capcut") capcut = args[i + 1];
}

int fails = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    if (!ok) fails++;
}
JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
Args A(string json) => new(J(json));

var work = Path.Combine(Path.GetTempPath(), "AIVE-selftest-" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(work);
Console.WriteLine("work dir: " + work);

// ---- generated media: 4 BMP photos + a WAV tone
var gen = Path.Combine(work, "media");
Directory.CreateDirectory(gen);
for (int i = 0; i < 4; i++) WriteBmp(Path.Combine(gen, $"photo{i}.bmp"), 320 + i * 40, 240, (byte)(60 * i));
var wav = audio ?? Path.Combine(work, "tone.wav");
if (audio == null) WriteWav(wav, 7.5);
var mediaFolder = media ?? gen;
string Esc(string p) => p.Replace("\\", "\\\\");

// ---- every timeline editor
foreach (var ed in Editors.All.Where(e => e.Id != "capcut"))
{
    Console.WriteLine($"[{ed.Display}]");
    try
    {
        var ext = ed.DirectFormat?.Extension ?? ".aive.json";
        var file = Path.Combine(work, ed.Id, "test" + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var p = new TimelineProject(ed, file);
        p.Run("set_canvas", A("""{"ratio":"16:9"}"""));
        var added = p.Run("add_media", A($$"""{"paths":["{{Esc(mediaFolder)}}"],"photo_duration":2,"max_video_duration":4}""")) as JsonArray;
        Check(added is { Count: > 0 }, $"add_media added {added?.Count} clips");
        p.Run("add_audio", A($$"""{"path":"{{Esc(wav)}}","fit_to_video":true,"fade_in":1,"fade_out":2,"volume":0.8}"""));
        p.Run("add_text", A("""{"text":"שלום עולם","start":0.5,"duration":3,"font_size":14,"y":0.6,"color":"#FFD700","stroke_color":"#000000"}"""));
        p.Run("set_transitions", A("""{"all_main":true,"name":"Cross Dissolve","duration":0.6}"""));
        p.Run("add_motion", A("""{"all_main_photos":true,"preset":"alternate"}"""));
        var ov = p.Overview();
        var first = ov["tracks"]![0]!["segments"]![0]!["id"]!.ToString();
        p.Run("set_animations", A($$"""{"ids":["{{first}}"],"kind":"in","name":"Fade In","duration":0.5}"""));
        p.Run("edit_segments", A($$"""{"ids":["{{first}}"],"duration":3,"x":0.1,"scale":1.1}"""));
        p.Run("split_segment", A($$"""{"id":"{{first}}","at":1.0}"""));
        p.Snapshot("test");
        p.Save();
        var before = p.Overview()["tracks"]!.AsArray().Sum(t => t!["segments"]!.AsArray().Count);
        Check(before > 4, $"timeline has {before} segments, duration {p.Overview()["duration_s"]}s");
        // every written file must parse
        var outputs = new List<string> { file };
        outputs.AddRange(p.LastExports);
        foreach (var f in outputs)
        {
            bool okParse;
            try
            {
                if (f.EndsWith(".xml") || f.EndsWith(".fcpxml") || f.EndsWith(".mlt")) { XDocument.Load(f); okParse = true; }
                else if (f.EndsWith(".json") || f.EndsWith(".otio") || f.EndsWith(".osp")) { JsonNode.Parse(File.ReadAllText(f)); okParse = true; }
                else okParse = new FileInfo(f).Length > 50;
            }
            catch (Exception e) { Console.WriteLine("     " + e.Message); okParse = false; }
            Check(okParse, $"wrote {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)");
        }
        // direct formats: re-load from the editor file and compare
        if (ed.DirectFormat != null)
        {
            var re = new TimelineProject(ed, file);
            var after = re.Overview()["tracks"]!.AsArray().Sum(t => t!["segments"]!.AsArray().Count);
            Check(after == before, $"round-trip keeps {after}/{before} segments");
            var d0 = (double)p.Overview()["duration_s"]!; var d1 = (double)re.Overview()["duration_s"]!;
            Check(Math.Abs(d0 - d1) < 0.2, $"round-trip duration {d1}s vs {d0}s");
            var txt = re.Doc.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Kind == "text");
            Check(txt?.Text == "שלום עולם", "round-trip keeps the Hebrew title");
            re.Save();
        }
        var label = p.Undo();
        Check(label == "test", "undo restores the snapshot");
    }
    catch (Exception e)
    {
        Check(false, $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}

// ---- CapCut (on a COPY of a real draft)
if (capcut != null)
{
    Console.WriteLine("[CapCut]");
    var copy = Path.Combine(work, "capcut", Path.GetFileName(capcut.TrimEnd('\\')));
    CopyDir(capcut, copy);
    try
    {
        var cat = CapCutProject.Catalog;
        foreach (var c in CapCutCatalog.Categories) Console.WriteLine($"     catalog {c}: {cat.Count(c)}");
        var p = new CapCutProject(copy);
        var segs0 = p.Overview()["tracks"]!.AsArray().Sum(t => t!["segments"]!.AsArray().Count);
        p.Snapshot("cc-test");
        p.Run("add_media", A($$"""{"paths":["{{Esc(mediaFolder)}}"],"photo_duration":2.5}"""));
        p.Run("add_audio", A($$"""{"path":"{{Esc(wav)}}","fit_to_video":true,"fade_out":3}"""));
        p.Run("add_text", A("""{"text":"בדיקה","start":0,"duration":4,"stroke_color":"#000000"}"""));
        if (cat.Count("transition") > 0)
            p.Run("set_transitions", A($$"""{"all_main":true,"name":"{{cat.Search("transition", "", 1)[0]!["name"]}}","duration":0.5}"""));
        if (cat.Count("video_in") > 0)
        {
            var id = p.Overview()["tracks"]![0]!["segments"]![0]!["id"]!.ToString();
            p.Run("set_animations", A($$"""{"ids":["{{id}}"],"kind":"in","name":"{{cat.Search("video_in", "", 1)[0]!["name"]}}"}"""));
        }
        p.Run("add_motion", A("""{"all_main_photos":true,"preset":"alternate"}"""));
        p.Save();
        var d = Json.Load(p.MainFile)!;
        var ids = new HashSet<string>();
        foreach (var kv in d["materials"]!.AsObject())
            if (kv.Value is JsonArray arr) foreach (var m in arr) if (m?["id"] is { } mid) ids.Add(mid.ToString());
        var dangling = d["tracks"]!.AsArray().SelectMany(t => t!["segments"]!.AsArray())
            .SelectMany(s => new[] { s!["material_id"]!.ToString() }.Concat(s["extra_material_refs"]!.AsArray().Select(r => r!.ToString())))
            .Count(r => !ids.Contains(r));
        Check(dangling == 0, $"no dangling material refs ({dangling})");
        var segs1 = p.Overview()["tracks"]!.AsArray().Sum(t => t!["segments"]!.AsArray().Count);
        Check(segs1 > segs0, $"segments {segs0} → {segs1}");
        var tl = Path.Combine(copy, "Timelines", d["id"]!.ToString(), "draft_content.json");
        if (File.Exists(tl)) Check(File.ReadAllText(tl) == File.ReadAllText(p.MainFile), "timeline copy matches draft_content.json");
        Check(p.Undo() == "cc-test", "undo restores original");
        var segs2 = p.Overview()["tracks"]!.AsArray().Sum(t => t!["segments"]!.AsArray().Count);
        Check(segs2 == segs0, $"after undo: {segs2} segments");
    }
    catch (Exception e)
    {
        Check(false, $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}

// ---- settings must never write a plain-text key
{
    Console.WriteLine("[Settings]");
    var st = new Settings { ClaudeKey = "sk-ant-selftest-secret", GeminiKey = "AIza-selftest-secret" };
    var json = System.Text.Json.JsonSerializer.Serialize(st);
    Check(!json.Contains("selftest-secret"), "API keys are not serialized in plain text");
    var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
    Check(back.ClaudeKey == "sk-ant-selftest-secret" && back.GeminiKey == "AIza-selftest-secret", "encrypted keys round-trip");
}

// ---- agent: system prompt and tools must not change during a conversation (thinking blocks are bound to them)
{
    Console.WriteLine("[Agent]");
    // the agent writes the user's real settings file; keep a copy and put it back afterwards
    var settingsFile = Path.Combine(Paths.AppData, "settings.json");
    var settingsBackup = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null;
    try {
    var dir = Path.Combine(work, "agent");
    Directory.CreateDirectory(dir);
    var ed = Editors.Get("shotcut");
    var f1 = Path.Combine(dir, "one.mlt"); var f2 = Path.Combine(dir, "two.mlt");
    new TimelineProject(ed, f1); new TimelineProject(ed, f2);
    Settings.Current.AddRecent("shotcut|" + f1); Settings.Current.AddRecent("shotcut|" + f2);
    var fake = new FakeProvider();
    var events = new List<string>();
    var agent = new AIVideoEditor.AI.Agent(fake, (e, _) => events.Add(e));
    agent.Open("shotcut", f1);
    fake.Script.Enqueue([new("c1", "get_project_overview", [])]);
    fake.Script.Enqueue([new("c2", "switch_project", new JsonObject { ["name"] = "two" })]);
    fake.Script.Enqueue([new("c3", "add_media", new JsonObject { ["paths"] = new JsonArray(gen) })]);
    fake.Script.Enqueue([]);
    await agent.SendAsync("first request");
    fake.Script.Enqueue([new("c4", "get_project_overview", [])]);
    fake.Script.Enqueue([]);
    await agent.SendAsync("second request");
    Check(fake.Systems.Count >= 6, $"{fake.Systems.Count} model calls");
    Check(fake.Systems.Distinct().Count() == 1, "system prompt identical on every call");
    Check(fake.ToolSets.Distinct().Count() == 1, "tool list identical on every call");
    Check(agent.Project!.MainFile == f2 && new TimelineProject(ed, f2).Doc.Tracks.SelectMany(t => t.Clips).Count() == 4,
        "switch_project + add_media edited the second project");
    // a thinking-binding error from the API is recovered by restarting the conversation once
    fake.FailNext = "messages.9.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation.";
    fake.Script.Enqueue([]);
    await agent.SendAsync("third request");
    Check(events.Contains("info") && !events.Contains("error"), "signature error recovered automatically");
    } finally {
        if (settingsBackup != null) File.WriteAllText(settingsFile, settingsBackup);
        else File.Delete(settingsFile);
    }
}

// ---- OpenAI-compatible provider against a local mock server (request/response wire format)
{
    Console.WriteLine("[OpenAI-compatible provider]");
    var port = 18000 + Random.Shared.Next(1000);
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/");
    listener.Start();
    var bodies = new List<JsonNode>();
    var server = Task.Run(async () =>
    {
        for (int i = 0; i < 3; i++)
        {
            var ctx = await listener.GetContextAsync();
            string reply;
            if (ctx.Request.HttpMethod == "GET")
                reply = """{"data":[{"id":"big-model:free","supported_parameters":["tools"]},{"id":"no-tools:free","supported_parameters":["temperature"]},{"id":"paid-model","supported_parameters":["tools"]},{"id":"whisper-large:free"}]}""";
            else
            {
                using var sr = new StreamReader(ctx.Request.InputStream);
                var body = JsonNode.Parse(await sr.ReadToEndAsync())!;
                bodies.Add(body);
                reply = bodies.Count == 1
                    ? """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning":"x","tool_calls":[{"id":"call_A","type":"function","function":{"name":"get_project_overview","arguments":"{}"}}]}}]}"""
                    : """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"<think>hidden</think>הכל מוכן"}}]}""";
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    var svc = AIVideoEditor.AI.Services.Get("openrouter")!;
    var models = await AIVideoEditor.AI.OpenAiCompatProvider.ListModels(svc, "test-key", $"http://127.0.0.1:{port}/v1");
    Check(models.SequenceEqual(["big-model:free"]), $"model list keeps only free tool-capable models ({string.Join(",", models)})");
    var ranked = AIVideoEditor.AI.OpenAiCompatProvider.Rank(svc, ["liquid/lfm-3-1.7b:free", "baidu/ernie-5.5-flash-preview:free",
        "nvidia/nemotron-3-ultra-550b-a55b:free", "google/gemma-4-31b-it:free", "nvidia/nemotron-3.5-lightning:free"]);
    Check(ranked[0] == "nvidia/nemotron-3-ultra-550b-a55b:free" && ranked[1] == "google/gemma-4-31b-it:free" && ranked[^1] == "liquid/lfm-3-1.7b:free",
        "ranking prefers big models: " + string.Join(" > ", ranked.Take(3)));
    var settingsFile2 = Path.Combine(Paths.AppData, "settings.json");
    var settingsBackup2 = File.Exists(settingsFile2) ? File.ReadAllText(settingsFile2) : null;
    try
    {
        var f = Path.Combine(work, "oa.mlt");
        new TimelineProject(Editors.Get("shotcut"), f);
        var prov = new AIVideoEditor.AI.OpenAiCompatProvider(svc, "test-key", "big-model:free", $"http://127.0.0.1:{port}/v1");
        var said = new List<string>();
        var agent = new AIVideoEditor.AI.Agent(prov, (e, d) => { if (e == "assistant") said.Add(d?.ToString() ?? ""); });
        agent.Open("shotcut", f);
        await agent.SendAsync("מה יש בפרויקט?");
        await server;
        Check(bodies.Count == 2, $"{bodies.Count} chat requests");
        var second = bodies[1]["messages"]!.AsArray();
        Check(bodies[0]["messages"]![0]!["content"]!.ToString() == second[0]!["content"]!.ToString(), "same system message on both requests");
        var echo = second.FirstOrDefault(m => m!["role"]!.ToString() == "assistant");
        Check(echo?["tool_calls"]?[0]?["id"]?.ToString() == "call_A" && echo?["reasoning"] == null, "assistant tool call echoed without extra fields");
        var tool = second.FirstOrDefault(m => m!["role"]!.ToString() == "tool");
        Check(tool?["tool_call_id"]?.ToString() == "call_A" && tool?["content"]?.ToString().Contains("Shotcut") == true, "tool result sent back with the right id");
        Check(bodies[0]["tools"]!.AsArray().Count == AIVideoEditor.AI.ToolDefs.Global.Count + AIVideoEditor.AI.ToolDefs.Project.Count, "all tools offered");
        Check(said.SequenceEqual(["הכל מוכן"]), "final answer shown, <think> removed");
    }
    finally
    {
        if (settingsBackup2 != null) File.WriteAllText(settingsFile2, settingsBackup2); else File.Delete(settingsFile2);
        listener.Stop();
    }
}

// ---- loose arguments, quota fallback, automatic model choice
{
    Console.WriteLine("[Robustness]");
    var la = new Args(JsonNode.Parse("""{"b1":"true","b2":"no","b3":1,"l1":"[\"a\",\"b\"]","l2":"single","l3":["x","y"]}""")!.AsObject());
    Check(la.B("b1") == true && la.B("b2") == false && la.B("b3") == true, "booleans given as text/numbers are accepted");
    Check(la.L("l1").SequenceEqual(["a", "b"]) && la.L("l2").SequenceEqual(["single"]) && la.L("l3").SequenceEqual(["x", "y"]),
        "lists given as a JSON string or a single value are accepted");

    var settingsFile3 = Path.Combine(Paths.AppData, "settings.json");
    var settingsBackup3 = File.Exists(settingsFile3) ? File.ReadAllText(settingsFile3) : null;
    try
    {
        var f = Path.Combine(work, "fb.mlt");
        new TimelineProject(Editors.Get("shotcut"), f);
        var broke = new FakeProvider { FailNext = "x", FailWith = new AIVideoEditor.AI.AiException("Groq error 429: rate limit reached", 429) };
        var good = new FakeProvider();
        var events = new List<(string, object?)>();
        var agent = new AIVideoEditor.AI.Agent(broke, (e, d) => events.Add((e, d))) { ProviderId = "groq" };
        var asked = new List<string>();
        agent.NextProvider = tried => { asked.AddRange(tried); return tried.Contains("gemini") ? null : ("gemini", good); };
        agent.Open("shotcut", f);
        good.Script.Enqueue([]);
        await agent.SendAsync("hello");
        Check(agent.ProviderId == "gemini" && good.Systems.Count == 1, "out-of-quota service replaced by the next one");
        Check(events.Any(e => e.Item1 == "provider") && !events.Any(e => e.Item1 == "error"), "the switch is reported, no error shown");
        Check(asked.Contains("groq"), "the failing service is not offered again");
        // when every service is exhausted the error surfaces instead of looping
        var dead = new FakeProvider { FailNext = "x", FailWith = new AIVideoEditor.AI.AiException("quota exceeded", 429) };
        var agent2 = new AIVideoEditor.AI.Agent(dead, (_, _) => { }) { ProviderId = "gemini", NextProvider = _ => null };
        agent2.Open("shotcut", f);
        bool threw = false;
        try { await agent2.SendAsync("hello"); } catch (AIVideoEditor.AI.AiException) { threw = true; }
        Check(threw, "with no other service the quota error is shown");
    }
    finally
    {
        if (settingsBackup3 != null) File.WriteAllText(settingsFile3, settingsBackup3); else File.Delete(settingsFile3);
    }

    // empty model: the provider asks the service for its models and uses the best one
    var port = 19000 + Random.Shared.Next(1000);
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/");
    listener.Start();
    string? usedModel = null;
    var server = Task.Run(async () =>
    {
        for (int i = 0; i < 2; i++)
        {
            var ctx = await listener.GetContextAsync();
            string reply = """{"data":[{"id":"tiny-1b"},{"id":"big-70b"}]}""";
            if (ctx.Request.HttpMethod == "POST")
            {
                using var sr = new StreamReader(ctx.Request.InputStream);
                usedModel = JsonNode.Parse(await sr.ReadToEndAsync())!["model"]!.ToString();
                reply = """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""";
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    var auto = new AIVideoEditor.AI.OpenAiCompatProvider(AIVideoEditor.AI.Services.Get("groq")!, "k", "", $"http://127.0.0.1:{port}/v1");
    auto.AddUser("hi");
    await auto.NextAsync("sys", [], CancellationToken.None);
    await server;
    listener.Stop();
    Check(usedModel == "big-70b", $"automatic model choice used '{usedModel}'");
}

// ---- optional: leave a finished demo project (used for the README screenshot)
var demoIdx = Array.IndexOf(args, "--demo");
if (demoIdx >= 0 && demoIdx + 1 < args.Length)
{
    var demoFile = Path.GetFullPath(args[demoIdx + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(demoFile)!);
    var demoMedia = Path.Combine(Path.GetDirectoryName(demoFile)!, "media");
    Directory.CreateDirectory(demoMedia);
    foreach (var f in Directory.GetFiles(gen)) File.Copy(f, Path.Combine(demoMedia, Path.GetFileName(f)), true);
    var demoWav = Path.Combine(demoMedia, "song.wav");
    File.Copy(wav, demoWav, true);
    var dp = new TimelineProject(Editors.Get("shotcut"), demoFile);
    dp.Run("add_media", A($$"""{"paths":["{{Esc(demoMedia)}}"],"photo_duration":2}"""));
    dp.Run("add_audio", A($$"""{"path":"{{Esc(demoWav)}}","fit_to_video":true,"fade_out":2}"""));
    dp.Run("add_text", A("""{"text":"הטיול המשפחתי","start":0,"duration":3,"font_size":14,"y":0.6}"""));
    dp.Run("set_transitions", A("""{"all_main":true,"name":"Cross Dissolve","duration":0.6}"""));
    dp.Run("add_motion", A("""{"all_main_photos":true,"preset":"alternate"}"""));
    dp.Save();
    Console.WriteLine("demo project: " + demoFile);
}

Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;

static void WriteBmp(string path, int w, int h, byte shade)
{
    int row = (w * 3 + 3) & ~3, size = 54 + row * h;
    var b = new byte[size];
    void I32(int o, int v) => BitConverter.GetBytes(v).CopyTo(b, o);
    b[0] = (byte)'B'; b[1] = (byte)'M'; I32(2, size); I32(10, 54); I32(14, 40); I32(18, w); I32(22, h);
    b[26] = 1; b[28] = 24; I32(34, row * h);
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = 54 + y * row + x * 3;
            b[o] = (byte)(x * 255 / w); b[o + 1] = shade; b[o + 2] = (byte)(y * 255 / h);
        }
    File.WriteAllBytes(path, b);
}

static void WriteWav(string path, double seconds)
{
    int rate = 22050, n = (int)(rate * seconds);
    using var bw = new BinaryWriter(File.Create(path));
    bw.Write("RIFF"u8); bw.Write(36 + n * 2); bw.Write("WAVE"u8); bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1);
    bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16); bw.Write("data"u8); bw.Write(n * 2);
    for (int i = 0; i < n; i++) bw.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000));
}

static void CopyDir(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
}

sealed class FakeProvider : AIVideoEditor.AI.IAiProvider
{
    public Queue<List<AIVideoEditor.AI.ToolCall>> Script { get; } = new();
    public List<string> Systems { get; } = [];
    public List<string> ToolSets { get; } = [];
    public string? FailNext { get; set; }
    public Exception? FailWith { get; set; }
    public string Display => "fake";
    public void Reset() { }
    public void AddUser(string text) { }
    public void AddToolResults(List<AIVideoEditor.AI.ToolOutput> results) { }
    public Task<AIVideoEditor.AI.AiTurn> NextAsync(string system, List<AIVideoEditor.AI.ToolDef> tools, CancellationToken ct)
    {
        if (FailNext is { } msg) { FailNext = null; throw FailWith ?? new InvalidOperationException(msg); }
        Systems.Add(system);
        ToolSets.Add(string.Join(",", tools.Select(t => t.Name + ":" + t.Properties.ToJsonString())));
        var calls = Script.Count > 0 ? Script.Dequeue() : [];
        return Task.FromResult(new AIVideoEditor.AI.AiTurn(calls.Count == 0 ? ["done"] : [], calls, "end_turn"));
    }
}
