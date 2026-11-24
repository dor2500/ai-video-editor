using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace AIVideoEditor.Core.CapCut;

public sealed class CatalogItem
{
    public required string Name { get; init; }
    public required string EffectId { get; init; }
    public required string ResourceId { get; init; }
    public string Path { get; init; } = "";
    public string CategoryId { get; init; } = "";
    public long DurationUs { get; init; }
    public bool IsOverlap { get; init; } = true;
    public bool Vip { get; init; }
    public bool UsedBefore { get; init; }
}

/// <summary>
/// Names → CapCut resource ids. Built on the user's PC from (1) effects already used in their drafts
/// (CapCut has those downloaded) and (2) CapCut's own cached panel lists in its resource database.
/// Nothing third-party is bundled.
/// </summary>
public sealed class CapCutCatalog
{
    public static readonly string[] Categories =
        ["transition", "video_in", "video_out", "video_combo", "text_in", "text_out", "text_loop", "filter", "effect", "font"];

    readonly Dictionary<string, List<CatalogItem>> items = Categories.ToDictionary(c => c, _ => new List<CatalogItem>());
    readonly HashSet<(string, string)> seen = [];

    public CapCutCatalog()
    {
        try { ScanDrafts(); } catch { }
        try { ScanCache(); } catch { }
    }

    void Add(string cat, CatalogItem it, bool front = false)
    {
        if (string.IsNullOrEmpty(it.EffectId) || !seen.Add((cat, it.EffectId))) return;
        if (front) items[cat].Insert(0, it); else items[cat].Add(it);
    }

    void ScanDrafts()
    {
        if (!Directory.Exists(CapCutLibrary.DraftsRoot)) return;
        foreach (var dir in Directory.GetDirectories(CapCutLibrary.DraftsRoot))
        {
            var f = System.IO.Path.Combine(dir, "draft_content.json");
            if (!File.Exists(f)) continue;
            JsonNode? d;
            try { d = Json.Load(f); } catch { continue; }
            var m = d?["materials"];
            if (m == null) continue;
            foreach (var t in m["transitions"]?.AsArray() ?? [])
                Add("transition", new CatalogItem
                {
                    Name = t.Str("name"), EffectId = t.Str("effect_id"), ResourceId = t.Str("resource_id"),
                    Path = t.Str("path"), CategoryId = t.Str("category_id"), DurationUs = t.Long("duration", 500000),
                    IsOverlap = t.Bool("is_overlap", true), UsedBefore = true,
                }, true);
            foreach (var ma in m["material_animations"]?.AsArray() ?? [])
            {
                bool text = ma.Str("type") == "sticker_animation";
                foreach (var a in ma?["animations"]?.AsArray() ?? [])
                {
                    var cat = (a.Str("type"), text) switch
                    {
                        ("in", false) => "video_in", ("out", false) => "video_out", ("group", false) => "video_combo",
                        ("in", true) => "text_in", ("out", true) => "text_out", ("loop", true) => "text_loop", _ => null,
                    };
                    if (cat == null) continue;
                    Add(cat, new CatalogItem
                    {
                        Name = a.Str("name"), EffectId = a.Str("id"), ResourceId = a.Str("resource_id"), Path = a.Str("path"),
                        CategoryId = a.Str("category_id"), DurationUs = a.Long("duration", 500000), UsedBefore = true,
                    }, true);
                }
            }
            foreach (var e in m["video_effects"]?.AsArray() ?? [])
                Add("effect", new CatalogItem
                {
                    Name = e.Str("name"), EffectId = e.Str("effect_id"), ResourceId = e.Str("resource_id"), Path = e.Str("path"),
                    UsedBefore = true,
                }, true);
            foreach (var e in m["effects"]?.AsArray() ?? [])
                if (e.Str("type") == "filter")
                    Add("filter", new CatalogItem
                    {
                        Name = e.Str("name"), EffectId = e.Str("effect_id"), ResourceId = e.Str("resource_id"), Path = e.Str("path"),
                        UsedBefore = true,
                    }, true);
        }
    }

    void ScanCache()
    {
        var root = System.IO.Path.Combine(CapCutLibrary.UserData, "Cache", "ressdk_db");
        if (!Directory.Exists(root)) return;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIVideoEditor-rp");
        Directory.CreateDirectory(tmp);
        foreach (var db in Directory.GetFiles(root, "rp.db", SearchOption.AllDirectories))
        {
            // copy with WAL so the cache is read as CapCut sees it, without touching the original
            var copy = System.IO.Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".db");
            try
            {
                File.Copy(db, copy, true);
                if (File.Exists(db + "-wal")) File.Copy(db + "-wal", copy + "-wal", true);
                using (var con = new SqliteConnection($"Data Source={copy}"))
                {
                    con.Open();
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = "select response_body from http_cache";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (r.IsDBNull(0)) continue;
                        var v = r.GetValue(0);
                        var s = v is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : v.ToString() ?? "";
                        if (!s.Contains("common_attr")) continue;
                        try { Walk(JsonNode.Parse(s)); } catch { }
                    }
                }
                SqliteConnection.ClearAllPools();
            }
            catch { }
            finally
            {
                foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" })
                    try { File.Delete(f); } catch { }
            }
        }
    }

    void Walk(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o["common_attr"] is JsonObject ca && ca.Str("effect_id") is { Length: > 0 } eid)
            {
                var type = ca.Long("effect_type", -1);
                var anim = o["animation"].Str("animation_type");
                var textAnim = o["text_animation"].Str("animation_type");
                string? cat = type switch
                {
                    13 => anim switch { "video_in" => "video_in", "video_out" => "video_out", "video_group" => "video_combo", _ => null },
                    18 => textAnim switch { "in" => "text_in", "out" => "text_out", "loop" => "text_loop", _ => null },
                    22 => "filter",
                    10 => "font",
                    _ => null,
                };
                if (cat != null)
                {
                    var third = ca.Str("third_resource_id_str");
                    var extra = ca.Str("extra");
                    bool vip = extra.Contains("\"is_vip\":true");
                    long dur = (long)(o["text_animation"].Dbl("duration", 0) * 1000);
                    Add(cat, new CatalogItem
                    {
                        Name = ca.Str("title"), EffectId = eid,
                        ResourceId = string.IsNullOrEmpty(third) ? eid : third,
                        DurationUs = dur > 0 ? dur : 500_000, Vip = vip,
                    });
                }
            }
            foreach (var kv in o) Walk(kv.Value);
        }
        else if (n is JsonArray a)
            foreach (var x in a) Walk(x);
    }

    public int Count(string cat) => items.TryGetValue(cat, out var l) ? l.Count : 0;

    public JsonArray Search(string cat, string query, int limit)
    {
        if (!items.TryGetValue(cat, out var list))
            throw new EditException($"Unknown category '{cat}'. Use one of: {string.Join(", ", Categories)}");
        var q = (query ?? "").Trim().ToLowerInvariant();
        var res = new JsonArray();
        foreach (var it in list)
        {
            if (q.Length > 0 && !it.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            res.Add(new JsonObject
            {
                ["name"] = it.Name, ["vip"] = it.Vip, ["used_before"] = it.UsedBefore,
                ["default_duration_s"] = T.Sec(it.DurationUs),
            });
            if (res.Count >= limit) break;
        }
        if (list.Count == 0)
            res.Add(new JsonObject
            {
                ["note"] = cat == "transition"
                    ? "No transitions known yet. Apply any transition once in CapCut (any project) and the app learns it."
                    : $"No '{cat}' items cached yet. Open that panel in CapCut once (it downloads the list) and try again.",
            });
        return res;
    }

    public CatalogItem Get(string cat, string name)
    {
        if (!items.TryGetValue(cat, out var list)) throw new EditException($"Unknown category '{cat}'");
        var n = name.Trim();
        return list.FirstOrDefault(i => i.Name.Trim().Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(i => i.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
               ?? throw new EditException($"No {cat} named '{name}'. Call search_catalog first.");
    }
}
