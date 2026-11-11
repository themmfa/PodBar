namespace PodBar;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool silent = args.Contains("--silent");

        // Design check: PodBar.exe --preview C:\some\folder  (writes PNGs of the card, then exits)
        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length)
        {
            PopupForm.RenderPreviews(args[preview + 1]);
            return 0;
        }

        // PodBar.exe --uninstall   (what Windows' "Installed apps" list runs)
        if (args.Contains("--uninstall")) return Installer.Uninstall(silent);

        // PodBar-Setup.exe, or PodBar.exe --install
        if (args.Contains("--install") || Installer.StartedAsSetup) return Installer.Install(silent);

        using var mutex = new Mutex(true, Installer.AppName + ".SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance) return 0;

        // Optional: PodBar.exe --log C:\path\to\log.txt
        int i = Array.IndexOf(args, "--log");
        if (i >= 0 && i + 1 < args.Length) Log.Path = args[i + 1];

        Application.Run(new TrayApp());
        return 0;
    }
}

static class Log
{
    public static string? Path;
    static readonly object Gate = new();

    public static void Write(string message)
    {
        if (Path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { /* logging must never break the app */ }
        }
    }
}
