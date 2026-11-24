using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace AIVideoEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstall(e.Args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase)));
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ev) =>
        {
            MessageBox.Show(ev.Exception.Message, "AI Video Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        base.OnStartup(e);
    }

    /// <summary>Removes a per-user install made by AIVideoEditor-Setup.exe. Settings/backups in AppData are kept.</summary>
    static void Uninstall(bool silent)
    {
        const string name = "AI Video Editor";
        if (!silent && MessageBox.Show($"להסיר את {name}?\n(ההגדרות והגיבויים נשארים)\n\nRemove {name}? (settings and backups are kept)",
                name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var lnk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name + ".lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk"),
                 })
            try { File.Delete(lnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVideoEditor", false); } catch { }
        // only remove the folder if it is the installer's folder (never a portable copy's folder)
        if (dir.EndsWith(Path.Combine("Programs", name), StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        if (!silent) MessageBox.Show($"{name} הוסר / was removed.", name);
    }
}
