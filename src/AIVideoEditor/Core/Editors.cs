using AIVideoEditor.Core.CapCut;
using AIVideoEditor.Core.Formats;
using AIVideoEditor.Core.Timeline;

namespace AIVideoEditor.Core;

/// <summary>One supported video editor.</summary>
public sealed class EditorDef
{
    public required string Id { get; init; }
    public required string Display { get; init; }
    /// <summary>Process image name (for "is it running / close it").</summary>
    public string? Process { get; init; }
    /// <summary>Candidate executables (env vars allowed). The first that exists is used.</summary>
    public string[] Exes { get; init; } = [];
    /// <summary>When set, the editor's own project file is loaded and saved directly.</summary>
    public ITimelineFormat? DirectFormat { get; init; }
    /// <summary>Import files written on every save (export-only editors).</summary>
    public ITimelineFormat[] Exports { get; init; } = [];
    public string ImportHint { get; init; } = "";
    /// <summary>Extensions of the editor's own project files, for discovering projects on disk.</summary>
    public string[] ProjectExts { get; init; } = [];
    public string[] ProjectDirs { get; init; } = [];

    public string? FindExe()
    {
        foreach (var e in Exes)
        {
            var p = Environment.ExpandEnvironmentVariables(e);
            if (p.Contains('*'))
            {
                var dir = Path.GetDirectoryName(p)!;
                var pattern = Path.GetFileName(p);
                var parent = Path.GetDirectoryName(dir);
                try
                {
                    // allow one wildcard folder level, e.g. "...\Adobe\Adobe Premiere Pro *\Adobe Premiere Pro.exe"
                    if (dir.Contains('*') && parent != null && Directory.Exists(parent))
                    {
                        var hit = Directory.GetDirectories(parent, Path.GetFileName(dir))
                            .OrderByDescending(x => x).Select(d => Path.Combine(d, pattern)).FirstOrDefault(File.Exists);
                        if (hit != null) return hit;
                    }
                }
                catch { }
                continue;
            }
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public bool Installed => Id == "capcut" ? CapCutLibrary.Installed : FindExe() != null;
    public bool Running => Process != null && Proc.Running(Process);
    public bool IsExportOnly => Id != "capcut" && DirectFormat == null;
}

public static class Editors
{
    const string PF = "%ProgramFiles%", LAD = "%LOCALAPPDATA%";

    public static readonly string ProjectsDir = Path.Combine(Paths.Documents, "AI Video Editor");

    public static readonly List<EditorDef> All =
    [
        new()
        {
            Id = "capcut", Display = "CapCut", Process = "CapCut.exe",
        },
        new()
        {
            Id = "camtasia", Display = "Camtasia", Process = "Camtasia*.exe",
            Exes =
            [
                $@"{PF}\TechSmith\Camtasia *\CamtasiaStudio.exe",
                $@"{PF}\TechSmith\Camtasia *\Camtasia.exe",
                $@"{PF}\TechSmith\Camtasia\CamtasiaStudio.exe",
            ],
            DirectFormat = new CamtasiaFormat(), ProjectExts = [".tscproj"],
            ProjectDirs = [Path.Combine(Paths.Documents, "Camtasia"), Paths.Documents, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))],
        },
        new()
        {
            Id = "shotcut", Display = "Shotcut", Process = "shotcut.exe",
            Exes = [$@"{PF}\Shotcut\shotcut.exe", $@"{LAD}\Programs\Shotcut\shotcut.exe"],
            DirectFormat = new MltFormat(), ProjectExts = [".mlt"],
            ProjectDirs = [Paths.Documents, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))],
        },
        new()
        {
            Id = "openshot", Display = "OpenShot", Process = "openshot-qt.exe",
            Exes = [$@"{PF}\OpenShot Video Editor\openshot-qt.exe"],
            DirectFormat = new OpenShotFormat(), ProjectExts = [".osp"],
            ProjectDirs = [Paths.Documents, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openshot_qt")],
        },
        new()
        {
            Id = "kdenlive", Display = "Kdenlive", Process = "kdenlive.exe",
            Exes = [$@"{PF}\kdenlive\bin\kdenlive.exe", $@"{LAD}\Programs\kdenlive\bin\kdenlive.exe"],
            Exports = [new OtioWriter()], ImportHint = "File → Import → OpenTimelineIO, pick the .otio file",
        },
        new()
        {
            Id = "premiere", Display = "Adobe Premiere Pro", Process = "Adobe Premiere Pro.exe",
            Exes = [$@"{PF}\Adobe\Adobe Premiere Pro *\Adobe Premiere Pro.exe"],
            Exports = [new XmemlWriter()], ImportHint = "File → Import, pick the .xml file (Final Cut Pro XML)",
        },
        new()
        {
            Id = "resolve", Display = "DaVinci Resolve", Process = "Resolve.exe",
            Exes = [$@"{PF}\Blackmagic Design\DaVinci Resolve\Resolve.exe"],
            Exports = [new FcpxmlWriter(), new OtioWriter()],
            ImportHint = "File → Import → Timeline, pick the .fcpxml (or .otio) file",
        },
        new()
        {
            Id = "finalcut", Display = "Final Cut Pro", Exports = [new FcpxmlWriter()],
            ImportHint = "File → Import → XML, pick the .fcpxml file (on the Mac)",
        },
        new()
        {
            Id = "vegas", Display = "VEGAS Pro", Process = "vegas*.exe",
            Exes = [$@"{PF}\VEGAS\VEGAS Pro *\vegas*.exe"],
            Exports = [new XmemlWriter(), new EdlWriter()],
            ImportHint = "File → Import → Final Cut Pro 7 XML (the .xml file); EDL as fallback",
        },
        new()
        {
            Id = "filmora", Display = "Wondershare Filmora", Process = "Wondershare Filmora.exe",
            Exes = [$@"{PF}\Wondershare\Wondershare Filmora\Wondershare Filmora.exe"],
            Exports = [new XmemlWriter(), new EdlWriter()],
            ImportHint = "File → Import → Import with Instant Mode / XML (version dependent); media + EDL as fallback",
        },
        new()
        {
            Id = "other", Display = "Other editor (export all formats)",
            Exports = [new FcpxmlWriter(), new XmemlWriter(), new OtioWriter(), new EdlWriter(), new MltFormat()],
            ImportHint = "most editors import one of: .fcpxml, .xml (FCP7), .otio, .edl or .mlt",
        },
    ];

    public static EditorDef Get(string id) =>
        All.FirstOrDefault(e => e.Id == id) ?? throw new EditException($"Unknown editor '{id}'");

    public sealed record ProjectInfo(string Editor, string Name, string Path, DateTime Modified, double DurationS);

    /// <summary>Projects the app can open for an editor.</summary>
    public static List<ProjectInfo> Projects(string editorId)
    {
        var ed = Get(editorId);
        if (ed.Id == "capcut")
            return CapCutLibrary.List().Select(p => new ProjectInfo("capcut", p.Name, p.Folder, p.Modified, p.DurationS)).ToList();
        var res = new List<ProjectInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // our own timeline files for export-only editors
        var own = Path.Combine(ProjectsDir, ed.Id);
        if (Directory.Exists(own))
            foreach (var f in Directory.GetFiles(own, ed.IsExportOnly ? "*.aive.json" : "*.*")
                         .Where(f => ed.IsExportOnly || ed.ProjectExts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
                if (seen.Add(f)) res.Add(Info(ed, f));
        foreach (var dir in ed.ProjectDirs.Where(Directory.Exists))
            try
            {
                foreach (var ext in ed.ProjectExts)
                    foreach (var f in Directory.EnumerateFiles(dir, "*" + ext, new EnumerationOptions
                                 { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                        if (seen.Add(f)) res.Add(Info(ed, f));
            }
            catch { }
        foreach (var f in Settings.Current.RecentFiles.Where(r => r.StartsWith(ed.Id + "|")).Select(r => r[(ed.Id.Length + 1)..]))
            if (File.Exists(f) && seen.Add(f)) res.Add(Info(ed, f));
        return res.OrderByDescending(p => p.Modified).Take(200).ToList();
    }

    static ProjectInfo Info(EditorDef ed, string f)
    {
        var name = System.IO.Path.GetFileName(f);
        name = name.EndsWith(".aive.json", StringComparison.OrdinalIgnoreCase) ? name[..^10] : System.IO.Path.GetFileNameWithoutExtension(f);
        return new ProjectInfo(ed.Id, name, f, File.GetLastWriteTime(f), 0);
    }

    public static EditorProject Open(string editorId, string path)
    {
        var ed = Get(editorId);
        if (ed.Id == "capcut") return new CapCutProject(path);
        Settings.Current.AddRecent(ed.Id + "|" + path);
        return new TimelineProject(ed, path);
    }

    public static string Create(string editorId, string name, string ratio)
    {
        var ed = Get(editorId);
        if (ed.Id == "capcut") return CapCutLibrary.Create(name, ratio);
        var safe = new string(name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (safe == "") safe = "AI Project";
        var dir = System.IO.Path.Combine(ProjectsDir, ed.Id);
        Directory.CreateDirectory(dir);
        var ext = ed.DirectFormat?.Extension ?? ".aive.json";
        var path = System.IO.Path.Combine(dir, safe + ext);
        for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(dir, $"{safe} ({n}){ext}");
        var p = new TimelineProject(ed, path);
        p.Doc.Name = safe;
        p.Run("set_canvas", new Args(new System.Text.Json.Nodes.JsonObject { ["ratio"] = ratio }));
        p.Save();
        Settings.Current.AddRecent(ed.Id + "|" + path);
        return path;
    }

    public static void Launch(EditorDef ed, string? projectPath)
    {
        if (ed.Id == "capcut")
        {
            var exe = CapCutLibrary.Exe() ?? throw new EditException("CapCut is not installed");
            Proc.Start(exe);
            return;
        }
        var e = ed.FindExe() ?? throw new EditException($"{ed.Display} was not found on this PC");
        Proc.Start(e, ed.DirectFormat != null && projectPath != null ? $"\"{projectPath}\"" : null);
    }
}
