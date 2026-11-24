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
