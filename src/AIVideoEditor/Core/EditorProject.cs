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
    public bool? B(string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public bool B(string k, bool def) => B(k) ?? def;
    public List<string> L(string k) => o[k] is JsonArray a
        ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToString() ?? "").Where(s => s != "").ToList()
        : o[k] is JsonValue sv && sv.TryGetValue<string>(out var one) ? [one] : [];
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

    public void Snapshot(string label)
    {
        if (!File.Exists(MainFile)) return;
        Directory.CreateDirectory(BackupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dst = Path.Combine(BackupDir, stamp + Path.GetExtension(MainFile) + ".bak");
        File.Copy(MainFile, dst, true);
        File.WriteAllText(dst + ".label", label);
        foreach (var old in Directory.GetFiles(BackupDir, "*.bak").OrderBy(x => x).SkipLast(60))
        {
            try { File.Delete(old); File.Delete(old + ".label"); } catch { }
        }
    }

    public string Undo()
    {
        var files = Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir, "*.bak").OrderBy(x => x).ToList() : [];
        if (files.Count == 0) throw new EditException("Nothing to undo");
        var last = files[^1];
        var label = File.Exists(last + ".label") ? File.ReadAllText(last + ".label") : "";
        File.Copy(last, MainFile, true);
        Reload();
        Save(); // rewrite companion files (e.g. CapCut's timeline copy) from the restored state
        try { File.Delete(last); File.Delete(last + ".label"); } catch { }
        return label;
    }

    protected static JsonNode Obj(object o) => JsonSerializer.SerializeToNode(o, Json.Relaxed)!;
}
