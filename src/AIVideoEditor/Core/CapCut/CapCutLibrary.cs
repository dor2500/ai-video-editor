using System.Text.Json.Nodes;

namespace AIVideoEditor.Core.CapCut;

public static class CapCutLibrary
{
    public static readonly string Root = Path.Combine(Paths.LocalAppData, "CapCut");
    public static readonly string UserData = Path.Combine(Root, "User Data");
    public static readonly string DraftsRoot = Path.Combine(UserData, "Projects", "com.lveditor.draft");
    static string RootMeta => Path.Combine(DraftsRoot, "root_meta_info.json");

    public static string? Exe()
    {
        var apps = Path.Combine(Root, "Apps");
        if (!Directory.Exists(apps)) return null;
        var hit = Directory.GetFiles(apps, "CapCut.exe", SearchOption.AllDirectories)
            .OrderByDescending(f => f).FirstOrDefault(f => Path.GetDirectoryName(f) != apps);
        return hit ?? (File.Exists(Path.Combine(apps, "CapCut.exe")) ? Path.Combine(apps, "CapCut.exe") : null);
    }

    public static bool Installed => Directory.Exists(DraftsRoot) || Exe() != null;

    public sealed record Info(string Name, string Folder, DateTime Modified, double DurationS);

    public static List<Info> List()
    {
        var res = new List<Info>();
        if (!Directory.Exists(DraftsRoot)) return res;
        var meta = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in Json.Load(RootMeta)?["all_draft_store"]?.AsArray() ?? [])
                if (e != null) meta[Norm(e.Str("draft_fold_path"))] = e;
        }
        catch { }
        foreach (var dir in Directory.GetDirectories(DraftsRoot))
        {
            var content = Path.Combine(dir, "draft_content.json");
            if (!File.Exists(content)) continue;
            meta.TryGetValue(Norm(dir), out var e);
            var name = e.Str("draft_name");
            res.Add(new Info(string.IsNullOrEmpty(name) ? Path.GetFileName(dir) : name, dir,
                File.GetLastWriteTime(content), T.Sec(e.Long("tm_duration"))));
        }
        return res.OrderByDescending(p => p.Modified).ToList();
    }

    static string Norm(string p)
    {
        try { return Path.GetFullPath(p.Replace('/', '\\')).TrimEnd('\\'); } catch { return p; }
    }

    public static void UpdateRootMeta(string folder, long duration)
    {
        try
        {
            var r = Json.Load(RootMeta);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            foreach (var e in r?["all_draft_store"]?.AsArray() ?? [])
                if (e != null && Norm(e.Str("draft_fold_path")).Equals(Norm(folder), StringComparison.OrdinalIgnoreCase))
                {
                    e["tm_duration"] = duration;
                    e["tm_draft_modified"] = now;
                }
            Json.SaveAtomic(RootMeta, r!.ToJsonString(Json.Relaxed));
        }
        catch { }
    }

    /// <summary>Create an empty project by cloning the skeleton of the smallest existing one.</summary>
    public static string Create(string name, string ratio)
    {
        var safe = new string(name.Where(c => !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim();
        if (safe == "") safe = "AI Project";
        var folder = Path.Combine(DraftsRoot, safe);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(DraftsRoot, $"{safe} ({n})");
        var src = List().OrderBy(p => new FileInfo(Path.Combine(p.Folder, "draft_content.json")).Length).FirstOrDefault()?.Folder
                  ?? throw new EditException("No existing CapCut project to use as a template. Create one empty project in CapCut first.");
        var d = Json.Load(Path.Combine(src, "draft_content.json"))!.AsObject();
        d["id"] = Ids.CapCut();
        d["tracks"] = new JsonArray();
        d["duration"] = 0;
        var mats = d["materials"]!.AsObject();
        foreach (var k in mats.Select(kv => kv.Key).ToList())
            if (mats[k] is JsonArray) mats[k] = new JsonArray();
        Directory.CreateDirectory(folder);
        var tl = Path.Combine(folder, "Timelines", d.Str("id"));
        Directory.CreateDirectory(tl);
        foreach (var fn in new[] { "draft_agency_config.json", "draft_settings", "timeline_layout.json", "attachment_pc_common.json" })
            if (File.Exists(Path.Combine(src, fn))) File.Copy(Path.Combine(src, fn), Path.Combine(folder, fn));
        var text = d.ToJsonString(Json.Relaxed);
        Json.SaveAtomic(Path.Combine(folder, "draft_content.json"), text);
        Json.SaveAtomic(Path.Combine(tl, "draft_content.json"), text);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        var projJson = Path.Combine(src, "Timelines", "project.json");
        if (File.Exists(projJson))
        {
            var pj = Json.Load(projJson)!.AsObject();
            pj["id"] = Ids.CapCut();
            pj["main_timeline_id"] = d.Str("id");
            pj["create_time"] = now;
            pj["update_time"] = now;
            pj["timelines"] = new JsonArray(new JsonObject
            {
                ["create_time"] = now, ["id"] = d.Str("id"), ["is_marked_delete"] = false,
                ["name"] = "Timeline 01", ["update_time"] = now,
            });
            Json.SaveAtomic(Path.Combine(folder, "Timelines", "project.json"), pj.ToJsonString(Json.Relaxed));
        }
        var meta = Json.Load(Path.Combine(src, "draft_meta_info.json"))!.AsObject();
        var draftId = Ids.CapCut();
        meta["draft_id"] = draftId;
        meta["draft_name"] = Path.GetFileName(folder);
        meta["draft_fold_path"] = folder.Replace('\\', '/');
        meta["draft_root_path"] = DraftsRoot;
        meta["draft_materials"] = new JsonArray();
        meta["tm_duration"] = 0;
        meta["tm_draft_create"] = now;
        meta["tm_draft_modified"] = now;
        meta["draft_cover"] = "";
        Json.SaveAtomic(Path.Combine(folder, "draft_meta_info.json"), meta.ToJsonString(Json.Relaxed));
        try
        {
            var r = Json.Load(RootMeta)!.AsObject();
            var store = r.Arr("all_draft_store");
            var tmpl = store.Count > 0 ? store[0]!.DeepClone().AsObject() : new JsonObject();
            tmpl["draft_cover"] = "";
            tmpl["draft_fold_path"] = folder.Replace('\\', '/');
            tmpl["draft_id"] = draftId;
            tmpl["draft_json_file"] = Path.Combine(folder, "draft_content.json").Replace('\\', '/');
            tmpl["draft_name"] = Path.GetFileName(folder);
            tmpl["tm_draft_create"] = now;
            tmpl["tm_draft_modified"] = now;
            tmpl["tm_duration"] = 0;
            tmpl["draft_timeline_materials_size"] = 0;
            store.Insert(0, tmpl);
            Json.SaveAtomic(RootMeta, r.ToJsonString(Json.Relaxed));
        }
        catch { }
        var p = new CapCutProject(folder);
        p.Run("set_canvas", new Args(new JsonObject { ["ratio"] = ratio }));
        p.Save();
        return folder;
    }
}
