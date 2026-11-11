using System.Diagnostics;
using Microsoft.Win32;

namespace PodBar;

/// <summary>
/// Per-user install and uninstall, built into the exe so no separate installer tool is needed.
/// Installing copies the exe to %LocalAppData%\Programs\PodBar, adds a Start menu shortcut and registers
/// the app in Windows' "Installed apps" list. Nothing here needs administrator rights.
/// </summary>
public static class Installer
{
    public const string AppName = "PodBar";
    public const string LegacyName = "AirPodsTray"; // what the app was called before it had a name

    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");

    static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>True when this exe was started under a "…Setup.exe" name, i.e. the user double-clicked the installer.</summary>
    public static bool StartedAsSetup =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase);

    public static int Install(bool silent)
    {
        if (!silent)
        {
            var answer = MessageBox.Show(
                $"Install {AppName} {Version} for your Windows account?\n\n" +
                "It will be added to the Start menu and to Windows' installed apps list, and then started. " +
                "No administrator rights are needed.",
                $"{AppName} Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return 1;
        }

        try
        {
            StopRunningCopies();

            string source = Environment.ProcessPath!;
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase))
                CopyWithRetry(source, InstalledExe);

            CreateShortcut();
            RegisterUninstaller();
            MoveStartupEntry();

            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });

            if (!silent)
                MessageBox.Show(
                    $"{AppName} is installed and running.\n\n" +
                    "Look for its icon in the system tray (it may be under the ^ arrow). " +
                    "You can also start it from the Start menu, and remove it from Settings > Apps > Installed apps.",
                    $"{AppName} Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show($"Installation failed:\n\n{ex.Message}", $"{AppName} Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                Console.Error.WriteLine(ex);
            return 2;
        }
    }

    public static int Uninstall(bool silent)
    {
        bool removeSettings = false;
        if (!silent)
        {
            var answer = MessageBox.Show(
                $"Remove {AppName} from this PC?", $"Uninstall {AppName}", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return 1;

            removeSettings = MessageBox.Show(
                "Also delete your saved settings and shortcuts?", $"Uninstall {AppName}",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        try
        {
            StopRunningCopies();

            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                run?.DeleteValue(AppName, throwOnMissingValue: false);
                run?.DeleteValue(LegacyName, throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            if (removeSettings && Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, recursive: true);

            // A running exe cannot delete itself, so hand the folder removal to a short-lived command prompt.
            if (Directory.Exists(InstallDir))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });

            if (!silent)
                MessageBox.Show($"{AppName} has been removed.", $"Uninstall {AppName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show($"Uninstall failed:\n\n{ex.Message}", $"Uninstall {AppName}", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    /// <summary>Closes any other running copy (including the pre-rename AirPodsTray.exe) so files can be replaced.</summary>
    static void StopRunningCopies()
    {
        int self = Environment.ProcessId;
        foreach (string name in new[] { AppName, LegacyName, AppName + "-Setup" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == self) continue;
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch { /* already gone, or not ours to stop */ }
                }
            }
        }
    }

    static void CopyWithRetry(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300); // the old copy may still be shutting down
            }
        }
    }

    static void CreateShortcut()
    {
        // WScript.Shell is the built-in Windows component for writing .lnk files.
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(ShortcutPath);
        shortcut.TargetPath = InstalledExe;
        shortcut.WorkingDirectory = InstallDir;
        shortcut.Description = "AirPods controls for Windows";
        shortcut.IconLocation = InstalledExe + ",0";
        shortcut.Save();
    }

    static void RegisterUninstaller()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --silent");
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>If "Start with Windows" was on (under either name), point it at the installed copy.</summary>
    static void MoveStartupEntry()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run is null) return;
        bool wasOn = run.GetValue(AppName) is string || run.GetValue(LegacyName) is string;
        run.DeleteValue(LegacyName, throwOnMissingValue: false);
        if (wasOn) run.SetValue(AppName, $"\"{InstalledExe}\"");
    }
}
