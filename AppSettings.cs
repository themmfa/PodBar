using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PodBar;

/// <summary>When media is paused automatically.</summary>
public enum PauseWhen { Never = 0, OnePodOut = 1, BothPodsOut = 2 }

/// <summary>User preferences, saved to %AppData%\PodBar\settings.json.</summary>
public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(Installer.SettingsDir, "settings.json");

    // Where settings lived before the app was named PodBar; read once so nothing is lost on upgrade.
    static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Installer.LegacyName, "settings.json");

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = Installer.AppName;

    // General
    public bool ShowCardOnConnect { get; set; } = true;
    public bool FollowWindowsTheme { get; set; } = true;
    public bool LowBatteryAlert { get; set; } = true;

    /// <summary>When set, broadcasts from any other AirPods model are ignored. Empty = accept all.</summary>
    public string LockedModel { get; set; } = "";

    // Playback
    public PauseWhen PauseWhen { get; set; } = PauseWhen.OnePodOut;
    public bool AutoOutputSwitch { get; set; }
    public bool SmartMic { get; set; }

    /// <summary>
    /// Process names (without .exe) the smart microphone should not react to. By default it reacts to every
    /// app that opens a microphone, games included; apps switched off in the tray menu are listed here.
    /// </summary>
    public List<string> SmartMicIgnoredApps { get; set; } = [];

    // Global shortcuts, stored as System.Windows.Forms.Keys values (key + modifiers). 0 = none.
    public int MicHotkey { get; set; } = (int)(Keys.Control | Keys.Alt | Keys.M);
    public int VolumeUpHotkey { get; set; }
    public int VolumeDownHotkey { get; set; }
    public int ShowCardHotkey { get; set; }

    /// <summary>Raised after any setting is saved.</summary>
    public event Action? Changed;

    /// <summary>
    /// Lives in the per-user "Run" registry key rather than the JSON file,
    /// so it always reflects what Windows will actually do at sign-in.
    /// </summary>
    [JsonIgnore]
    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }

    /// <summary>If the app was moved since "Start with Windows" was turned on, point the entry at the new location.</summary>
    public void RepairStartupPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            // Prefer the installed copy, so running a stray portable copy does not hijack the startup entry.
            string target = File.Exists(Installer.InstalledExe) ? Installer.InstalledExe : Environment.ProcessPath!;
            string expected = $"\"{target}\"";

            // Carry over a startup entry made under the old app name.
            if (key?.GetValue(Installer.LegacyName) is string)
            {
                key.DeleteValue(Installer.LegacyName, throwOnMissingValue: false);
                key.SetValue(RunName, expected);
            }

            if (key?.GetValue(RunName) is string current && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(RunName, expected);
                Log.Write($"Startup entry updated to {expected}");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not update startup entry: {ex.Message}");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string? path = File.Exists(FilePath) ? FilePath : File.Exists(LegacyFilePath) ? LegacyFilePath : null;
            if (path is not null)
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read settings, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save settings: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>"Ctrl+Alt+M" style text for a stored shortcut.</summary>
    public static string DescribeHotkey(int value)
    {
        if (value == 0) return "None";
        var keys = (Keys)value;
        var parts = new List<string>();
        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");

        var code = keys & Keys.KeyCode;
        string name = code is >= Keys.D0 and <= Keys.D9 ? ((int)code - (int)Keys.D0).ToString() : code.ToString();
        parts.Add(name);
        return string.Join("+", parts);
    }
}
