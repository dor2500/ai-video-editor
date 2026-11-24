// AI Video Editor installer (NativeAOT, no admin rights needed).
//   AIVideoEditor-Setup.exe            interactive install / update
//   AIVideoEditor-Setup.exe /S         silent install
//   AIVideoEditor.exe --uninstall [/S] remove (registered in Apps & Features)
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static partial class Setup
{
    const string AppName = "AI Video Editor";
    const string ExeName = "AIVideoEditor.exe";
    const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVideoEditor";
    static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0.0";
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    static readonly string StartMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static readonly string Desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    const uint MB_OK = 0, MB_YESNO = 4, MB_ICONINFO = 0x40, MB_ICONQUESTION = 0x20, MB_ICONERROR = 0x10, MB_RTLREADING = 0x2000, MB_RIGHT = 0x80000;
    const int IDYES = 6;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hwnd, string text, string caption, uint type);

    static bool silent;
    static int Ask(string he, string en, uint type) =>
        silent ? IDYES : MessageBox(0, he + "\n\n" + en, AppName + " " + Version, type | MB_RTLREADING | MB_RIGHT);

    static int Main(string[] args)
    {
        silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        try
        {
            return Install();
        }
        catch (Exception e)
        {
            Ask("ההתקנה נכשלה:\n" + e.Message, "Setup failed: " + e.Message, MB_OK | MB_ICONERROR);
            return 1;
        }
    }

    static int Install()
    {
        bool update = File.Exists(Path.Combine(InstallDir, ExeName));
        var (he, en) = update
            ? ($"לעדכן את {AppName} לגרסה {Version}?", $"Update {AppName} to version {Version}?")
            : ($"להתקין את {AppName} {Version}?\n\nתיקייה: {InstallDir}\nיתווספו קיצורי דרך לתפריט התחל ולשולחן העבודה.",
               $"Install {AppName} {Version}?\nFolder: {InstallDir}\nStart menu and desktop shortcuts will be added.");
        if (Ask(he, en, MB_YESNO | MB_ICONQUESTION) != IDYES) return 2;

        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try { p.CloseMainWindow(); if (!p.WaitForExit(5000)) p.Kill(); } catch { }
        }
        Directory.CreateDirectory(InstallDir);
        var target = Path.Combine(InstallDir, ExeName);
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe")
                         ?? throw new InvalidOperationException("payload missing"))
        using (var dst = File.Create(target + ".new"))
            src.CopyTo(dst);
        File.Move(target + ".new", target, true);
        try { File.Delete(Path.Combine(InstallDir, "uninstall.exe")); } catch { } // left by early builds

        Shortcut(StartMenu, target);
        if (!update || File.Exists(Desktop)) Shortcut(Desktop, target);

        using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "dor2500");
            k.SetValue("DisplayIcon", target);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{target}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{target}\" --uninstall /S");
            k.SetValue("URLInfoAbout", "https://github.com/dor2500/ai-video-editor");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
        }

        if (!WebView2Installed() &&
            Ask("חסר רכיב Microsoft Edge WebView2 שהתוכנה צריכה. לפתוח את דף ההורדה של מיקרוסופט?",
                "The Microsoft Edge WebView2 Runtime is missing. Open Microsoft's download page?", MB_YESNO | MB_ICONQUESTION) == IDYES)
            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });

        if (silent) return 0;
        if (Ask("ההתקנה הסתיימה. לפתוח את התוכנה עכשיו?", "Installation finished. Launch now?", MB_YESNO | MB_ICONINFO) == IDYES)
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = InstallDir });
        return 0;
    }


    static bool WebView2Installed()
    {
        const string guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{guid}"),
                     (Registry.LocalMachine, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{guid}"),
                     (Registry.CurrentUser, $@"Software\Microsoft\EdgeUpdate\Clients\{guid}"),
                 })
        {
            using var k = hive.OpenSubKey(path);
            if (k?.GetValue("pv") is string pv && pv != "" && pv != "0.0.0.0") return true;
        }
        return false;
    }

    /// <summary>Creates a .lnk through the shell's own scripting object (no COM interop code in the AOT binary).</summary>
    static void Shortcut(string lnk, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        static string Q(string s) => s.Replace("'", "''");
        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Q(lnk)}');$s.TargetPath='{Q(target)}';" +
                 $"$s.WorkingDirectory='{Q(Path.GetDirectoryName(target)!)}';$s.IconLocation='{Q(target)},0';$s.Description='{AppName}';$s.Save()";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{ps}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(20000);
    }
}
