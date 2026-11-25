using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIVideoEditor.Core;

/// <summary>Typed access to a tool call's JSON arguments.</summary>
public sealed class Args(JsonObject o)
{
    public JsonObject Raw => o;
    public bool Has(string k) => o[k] is not null;
    public string? S(string k) => o[k] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    public string S(string k, string def) => S(k) ?? def;
    public double? D(string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }
    public double D(string k, double def) => D(k) ?? def;
    /// <summary>Booleans; weaker models often send "true"/"false"/"yes"/1 instead of JSON booleans.</summary>
    public bool? B(string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s))
            return s.Trim().ToLowerInvariant() switch { "true" or "yes" or "1" or "on" => true, "false" or "no" or "0" or "off" => false, _ => null };
        if (v.TryGetValue<int>(out var i)) return i != 0;
        if (v.TryGetValue<double>(out var d)) return d != 0;
        return null;
    }
    public bool B(string k, bool def) => B(k) ?? def;
    /// <summary>Lists; also accepts a single string or a JSON array encoded as a string (common with weaker models).</summary>
    public List<string> L(string k)
    {
        if (o[k] is JsonArray a)
            return a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToString() ?? "").Where(s => s != "").ToList();
        if (o[k] is JsonValue sv && sv.TryGetValue<string>(out var one))
        {
            var t = one.Trim();
            if (t.StartsWith('['))
                try { if (JsonNode.Parse(t) is JsonArray arr) return new Args(new JsonObject { [k] = arr.DeepClone() }).L(k); } catch { }
            return t == "" ? [] : [one];
        }
        return [];
    }
    public string Req(string k) => S(k) ?? throw new EditException($"Missing '{k}'");
    public double ReqD(string k) => D(k) ?? throw new EditException($"Missing number '{k}'");
}

/// <summary>Context the agent passes to project tools.</summary>
public sealed class ToolContext
{
    public required string RequestLabel { get; init; }
}

/// <summary>
/// One open project in some editor. Concrete classes edit the editor's own file format
/// (CapCut draft JSON, MLT XML for Shotcut/Kdenlive, OpenShot JSON) or a neutral timeline
/// that is exported for editors whose files cannot be edited safely.
/// </summary>
public abstract class EditorProject
{
    public abstract string EditorId { get; }
    public abstract string Name { get; }
    /// <summary>The file that is snapshotted for undo.</summary>
    public abstract string MainFile { get; }
    public abstract IReadOnlySet<string> SupportedTools { get; }
    /// <summary>Extra instructions for the AI about this editor.</summary>
    public virtual string Notes => "";

    public abstract void Reload();
    public abstract void Save();
    public abstract JsonNode Overview();
    public abstract JsonNode? Run(string tool, Args a);

    /// <summary>Files the editor writes besides MainFile (written on Save, restored on Undo by re-saving).</summary>
    public virtual string? AfterSaveMessage => null;

    string BackupDir
    {
        get
        {
            var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(MainFile.ToLowerInvariant())))[..12];
            return Path.Combine(Paths.Backups, EditorId, h);
        }
    }

    string RedoDir => Path.Combine(BackupDir, "redo");

    public bool CanUndo => Directory.Exists(BackupDir) && Directory.GetFiles(BackupDir, "*.bak").Length > 0;
    public bool CanRedo => Directory.Exists(RedoDir) && Directory.GetFiles(RedoDir, "*.bak").Length > 0;

    static void CopySnapshot(string src, string dstDir, string ext, string label)
    {
        Directory.CreateDirectory(dstDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dst = Path.Combine(dstDir, stamp + ext + ".bak");
        File.Copy(src, dst, true);
        File.WriteAllText(dst + ".label", label);
        foreach (var old in Directory.GetFiles(dstDir, "*.bak").OrderBy(x => x).SkipLast(60))
        {
            try { File.Delete(old); File.Delete(old + ".label"); } catch { }
        }
    }

    public void Snapshot(string label)
    {
        if (!File.Exists(MainFile)) return;
        CopySnapshot(MainFile, BackupDir, Path.GetExtension(MainFile), label);
        if (Directory.Exists(RedoDir))
            foreach (var f in Directory.GetFiles(RedoDir))
                try { File.Delete(f); } catch { }
    }

    public string Undo()
    {
        var files = Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir, "*.bak").OrderBy(x => x).ToList() : [];
        if (files.Count == 0) throw new EditException("Nothing to undo");
        var last = files[^1];
        var label = File.Exists(last + ".label") ? File.ReadAllText(last + ".label") : "";
        if (File.Exists(MainFile))
            CopySnapshot(MainFile, RedoDir, Path.GetExtension(MainFile), label);
        File.Copy(last, MainFile, true);
        Reload();
        Save(); // rewrite companion files (e.g. CapCut's timeline copy) from the restored state
        try { File.Delete(last); File.Delete(last + ".label"); } catch { }
        return label;
    }

    public string Redo()
    {
        var files = Directory.Exists(RedoDir) ? Directory.GetFiles(RedoDir, "*.bak").OrderBy(x => x).ToList() : [];
        if (files.Count == 0) throw new EditException("Nothing to redo");
        var last = files[^1];
        var label = File.Exists(last + ".label") ? File.ReadAllText(last + ".label") : "";
        if (File.Exists(MainFile))
            CopySnapshot(MainFile, BackupDir, Path.GetExtension(MainFile), label);
        File.Copy(last, MainFile, true);
        Reload();
        Save();
        try { File.Delete(last); File.Delete(last + ".label"); } catch { }
        return label;
    }

    protected static JsonNode Obj(object o) => JsonSerializer.SerializeToNode(o, Json.Relaxed)!;
}
