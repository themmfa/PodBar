using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PodBar;

/// <summary>Owns the tray icon, the popup card, the scanner and the automatic behaviours.</summary>
public sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    const int LowBatteryPercent = 20;

    readonly AppSettings _settings = AppSettings.Load();
    readonly AudioControl _audio = new();
    readonly NotifyIcon _tray = new() { Visible = true };
    readonly PopupForm _popup;
    readonly AirPodsScanner _scanner = new();
    readonly MediaController _media = new();
    readonly System.Windows.Forms.Timer _staleTimer = new() { Interval = 5000 };
    readonly ToolStripMenuItem _statusItem = new("Searching for AirPods…") { Enabled = false };

    AirPodsState? _state;
    DateTime _lastSeen;
    string _message = "Searching for AirPods…";
    string? _iconText;
    IntPtr _iconHandle;
    bool _lowBatteryAlerted;
    (bool Left, bool Right)? _ears; // last in-ear state acted on

    public TrayApp()
    {
        _popup = new PopupForm(_settings, _audio);
        _ = _popup.Handle; // create the window handle now so BeginInvoke and global shortcuts work

        ApplySettings();
        _settings.Changed += ApplySettings;
        _settings.RepairStartupPath();

        var linkItem = new ToolStripMenuItem("Connect");
        linkItem.Click += (_, _) => _popup.ToggleLink();
        var useMic = new ToolStripMenuItem("Use AirPods microphone");
        useMic.Click += (_, _) => _popup.ToggleMic();

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            var link = _popup.Link;
            linkItem.Text = link == AirPodsLink.Connected ? "Disconnect" : "Connect";
            linkItem.Enabled = link != AirPodsLink.NotPaired;

            var mic = _popup.MicStatus;
            useMic.Enabled = mic.CanSwitch;
            useMic.Checked = mic.Active;
            useMic.ShortcutKeyDisplayString = _settings.MicHotkey != 0 ? AppSettings.DescribeHotkey(_settings.MicHotkey) : null;
        };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(linkItem);
        menu.Items.Add(useMic);
        menu.Items.Add("Settings…", null, (_, _) => _popup.ShowCard(settingsView: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_popup.Visible) _popup.Hide(); else _popup.ShowCard();
        };

        _scanner.StateReceived += s => _popup.BeginInvoke(() => OnState(s));
        _scanner.Failed += error => _popup.BeginInvoke(() =>
        {
            _message = $"Bluetooth unavailable ({error}). Turn it on and restart.";
            SetState(null);
        });

        _staleTimer.Tick += (_, _) =>
        {
            if (_state is not null && DateTime.UtcNow - _lastSeen > StaleAfter) SetState(null);

            // While the AirPods are connected to this PC, also accept weaker broadcasts so fewer updates are missed.
            _scanner.MinRssi = AirPodsConnection.GetLink() == AirPodsLink.Connected
                ? AirPodsScanner.ConnectedMinRssi : AirPodsScanner.DefaultMinRssi;
        };
        _staleTimer.Start();

        Log.Write($"Playback: {_audio.GetVolume()?.ToString() ?? "none"} / {_audio.GetStatus(EDataFlow.Render)}");
        Log.Write($"Microphone: {_audio.GetStatus(EDataFlow.Capture)}");
        Log.Write($"Bluetooth link: {AirPodsConnection.GetLink()}, controllable driver endpoints: {AirPodsConnection.SetLink(true, dryRun: true)}");

        Log.Write($"Settings: pause when={_settings.PauseWhen}, auto output switch={_settings.AutoOutputSwitch}");
        MediaController.LogSessions();

        Render();
        _scanner.Start();
    }

    void ApplySettings()
    {
        _media.Enabled = _settings.PauseWhen != PauseWhen.Never;
        _media.OnlyWhenBothOut = _settings.PauseWhen == PauseWhen.BothPodsOut;
    }

    void OnState(AirPodsState s)
    {
        // "Only my AirPods": drop broadcasts from any other model.
        if (_settings.LockedModel != "" && s.Model != _settings.LockedModel) return;

        bool firstSighting = _state is null;
        _lastSeen = DateTime.UtcNow;
        SetState(s);
        CheckLowBattery(s);

        // React on the first packet that reports a change. Waiting for a second one to confirm cost about a
        // second (broadcasts arrive roughly once a second), and the in-ear flags proved stable in practice.
        var ears = (s.LeftInEar, s.RightInEar);
        if (_ears != ears)
        {
            var previous = _ears;
            _ears = ears;
            OnEarsChanged(previous, ears);
        }

        // Mimic the Mac/iPhone card: pop up briefly when the AirPods show up.
        if (firstSighting && _settings.ShowCardOnConnect && !_popup.Visible) _popup.ShowCard(autoHideMs: 6000);
    }

    void OnEarsChanged((bool Left, bool Right)? previous, (bool Left, bool Right) now)
    {
        _media.OnEars(now.Left, now.Right);

        // Automatic output switching: AirPods while worn, the previous device once both are out.
        if (!_settings.AutoOutputSwitch || previous is null) return;
        bool wasWorn = previous.Value.Left || previous.Value.Right;
        bool worn = now.Left || now.Right;
        if (wasWorn == worn) return;

        var output = _audio.GetStatus(EDataFlow.Render);
        Log.Write($"Output switching: worn={worn}, {output}");
        if (worn && output is { Present: true, Active: false }) _audio.SetAirPodsDefault(EDataFlow.Render, true);
        else if (!worn && output is { Active: true, CanSwitch: true }) _audio.SetAirPodsDefault(EDataFlow.Render, false);
    }

    /// <summary>One notification each time a pod that is not charging falls to the threshold.</summary>
    void CheckLowBattery(AirPodsState s)
    {
        bool leftLow = s.Left <= LowBatteryPercent && !s.LeftCharging;
        bool rightLow = s.Right <= LowBatteryPercent && !s.RightCharging;
        if (!leftLow && !rightLow)
        {
            // Only re-arm once both pods report a healthy level, so an unknown reading does not retrigger.
            if (s.Left > LowBatteryPercent && s.Right > LowBatteryPercent) _lowBatteryAlerted = false;
            return;
        }
        if (_lowBatteryAlerted || !_settings.LowBatteryAlert) return;

        _lowBatteryAlerted = true;
        string which = leftLow && rightLow ? $"Left {s.Left}%, right {s.Right}%"
                     : leftLow ? $"Left pod at {s.Left}%" : $"Right pod at {s.Right}%";
        _tray.ShowBalloonTip(5000, "AirPods battery low", which, ToolTipIcon.Warning);
    }

    void SetState(AirPodsState? s)
    {
        if (s is not null && s == _state) return;
        _state = s;
        if (s is null) _ears = null;
        Render();
    }

    void Render()
    {
        _popup.SetState(_state, _message);

        string summary = _state is null
            ? _message
            : $"{_state.Model}  L {Pct(_state.Left)}  R {Pct(_state.Right)}  Case {Pct(_state.Case)}";
        _statusItem.Text = summary;
        _tray.Text = summary.Length > 120 ? summary[..120] : summary;

        // Tray icon shows the lower of the two pods, since that one runs out first.
        int? lowest = (_state?.Left, _state?.Right) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => null,
        };
        SetIcon(lowest?.ToString() ?? "–", lowest is null ? Color.FromArgb(110, 110, 118)
                                         : lowest <= LowBatteryPercent ? Color.FromArgb(215, 60, 50)
                                         : Color.FromArgb(40, 150, 70));

        static string Pct(int? v) => v is int p ? $"{p}%" : "–";
    }

    void SetIcon(string text, Color background)
    {
        string key = text + background.ToArgb();
        if (key == _iconText) return;
        _iconText = key;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(background);
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var font = new Font("Segoe UI", text.Length >= 3 ? 12f : 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        }

        IntPtr handle = bmp.GetHicon();
        var oldIcon = _tray.Icon;
        _tray.Icon = Icon.FromHandle(handle);
        oldIcon?.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = handle;
    }

    protected override void ExitThreadCore()
    {
        _scanner.Stop();
        _staleTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
