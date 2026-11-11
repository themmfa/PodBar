using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PodBar;

/// <summary>Owns the tray icon, the popup card, the scanner and the automatic behaviours.</summary>
public sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CardAfterAbsence = TimeSpan.FromMinutes(2);
    const string Searching = "Searching for AirPods…";
    const int LowBatteryPercent = 20;

    readonly AppSettings _settings = AppSettings.Load();
    readonly AudioControl _audio = new();
    readonly NotifyIcon _tray = new() { Visible = true };
    readonly PopupForm _popup;
    readonly AirPodsScanner _scanner = new();
    readonly MediaController _media = new();
    readonly SmartMic _smartMic;
    readonly System.Windows.Forms.Timer _smartMicTimer = new() { Interval = 1500 };
    readonly System.Windows.Forms.Timer _staleTimer = new() { Interval = 5000 };
    readonly ToolStripMenuItem _statusItem = new("Searching for AirPods…") { Enabled = false };

    AirPodsState? _state;
    DateTime _lastSeen;
    string _message = Searching;
    string? _iconText;
    IntPtr _iconHandle;
    bool _lowBatteryAlerted;
    (bool Left, bool Right)? _ears; // last in-ear state acted on
    bool? _outputWorn;              // worn state the playback device was last matched to; null = not matched yet

    public TrayApp()
    {
        _smartMic = new SmartMic(_audio) { IgnoredApps = () => _settings.SmartMicIgnoredApps };
        _smartMicTimer.Tick += (_, _) => _smartMic.Tick();
        _popup = new PopupForm(_settings, _audio);
        _ = _popup.Handle; // create the window handle now so BeginInvoke and global shortcuts work

        ApplySettings();
        _settings.Changed += ApplySettings;
        _settings.RepairStartupPath();

        var linkItem = new ToolStripMenuItem("Connect");
        linkItem.Click += (_, _) => _popup.ToggleLink();
        var useMic = new ToolStripMenuItem("Use AirPods microphone");
        useMic.Click += (_, _) => _popup.ToggleMic();

        // Per-app switches for the smart microphone. Every app that has used a microphone since PodBar started
        // is listed and ticked (it triggers the smart mic); unticking one makes the smart mic ignore that app.
        var smartApps = new ToolStripMenuItem("Smart microphone apps");

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            smartApps.Visible = _settings.SmartMic;
            smartApps.DropDownItems.Clear();
            var names = new SortedSet<string>(_settings.SmartMicIgnoredApps, StringComparer.OrdinalIgnoreCase);
            names.UnionWith(_smartMic.SeenApps);
            foreach (string name in names)
            {
                bool ignored = _settings.SmartMicIgnoredApps.Contains(name, StringComparer.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(name) { Checked = !ignored };
                item.Click += (_, _) =>
                {
                    if (ignored) _settings.SmartMicIgnoredApps.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                    else _settings.SmartMicIgnoredApps.Add(name);
                    _settings.Save();
                };
                smartApps.DropDownItems.Add(item);
            }
            if (names.Count == 0)
                smartApps.DropDownItems.Add(new ToolStripMenuItem("Apps appear here once they use a microphone") { Enabled = false });

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
        menu.Items.Add(smartApps);
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
            _message = $"Bluetooth is off or unavailable ({error}). Waiting for it to come back…";
            SetState(null);
        });
        _scanner.Recovered += () =>
        {
            _message = Searching;
            Render();
        };

        _staleTimer.Tick += (_, _) =>
        {
            if (_state is not null && DateTime.UtcNow - _lastSeen > StaleAfter) SetState(null);

            // Bring scanning back if the Bluetooth radio went away (switched off, or reset after sleep).
            _scanner.EnsureRunning();

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
        var recorders = SmartMic.ActiveRecorders();
        Log.Write($"Smart mic={_settings.SmartMic}; apps recording now: {(recorders.Count == 0 ? "none" : string.Join(", ", recorders))}");

        Render();
        _scanner.Start();
    }

    void ApplySettings()
    {
        _media.Enabled = _settings.PauseWhen != PauseWhen.Never;
        _media.OnlyWhenBothOut = _settings.PauseWhen == PauseWhen.BothPodsOut;

        // The recording check only runs while the feature is on, so it costs nothing otherwise.
        _smartMic.Enabled = _settings.SmartMic;
        _smartMicTimer.Enabled = _settings.SmartMic;
    }

    async void OnState(AirPodsState s)
    {
        // "Only my AirPods": drop broadcasts from any other model.
        if (_settings.LockedModel != "" && s.Model != _settings.LockedModel) return;

        // The card pops up when the AirPods appear, but not when they merely reappear after a short gap
        // in reception (a weak signal can drop them for longer than the stale timeout).
        bool firstSighting = _state is null;
        bool longAbsence = _lastSeen == default || DateTime.UtcNow - _lastSeen > CardAfterAbsence;
        _lastSeen = DateTime.UtcNow;
        SetState(s);
        CheckLowBattery(s);

        if (firstSighting && longAbsence && _settings.ShowCardOnConnect && !_popup.Visible) _popup.ShowCard(autoHideMs: 6000);

        // React on the first packet that reports a change. Waiting for a second one to confirm cost about a
        // second (broadcasts arrive roughly once a second), and the in-ear flags proved stable in practice.
        var ears = (s.LeftInEar, s.RightInEar);
        Task pauseOrResume = Task.CompletedTask;
        if (_ears != ears)
        {
            _ears = ears;
            pauseOrResume = _media.OnEarsAsync(ears.LeftInEar, ears.RightInEar);
        }

        // When the pods come out, let the pause land before the sound moves to the speakers;
        // otherwise the music would play out loud for a moment.
        if (!ears.LeftInEar && !ears.RightInEar)
        {
            try { await pauseOrResume; } catch { /* logged by the media controller */ }
        }

        // Use the latest state: another broadcast may have been handled while waiting.
        if (_ears is var (left, right)) SyncOutput(worn: left || right);
    }

    /// <summary>
    /// Automatic output switching: AirPods while worn, the previous device once both pods are out.
    /// Runs on every broadcast and acts whenever the worn state differs from the one it last matched,
    /// which includes the first sighting. (Reacting only to a change it watched happen missed the case
    /// where the AirPods come out of the case and go straight into the ears.)
    /// It never repeats an action for the same worn state, so a manual device choice is left alone.
    /// </summary>
    void SyncOutput(bool worn)
    {
        if (!_settings.AutoOutputSwitch)
        {
            _outputWorn = null;
            return;
        }
        if (_outputWorn == worn) return;

        var output = _audio.GetStatus(EDataFlow.Render);
        if (worn)
        {
            if (!output.Present) return; // not connected to this PC yet; try again on the next broadcast
            if (!output.Active) _audio.SetAirPodsDefault(EDataFlow.Render, true);
        }
        else if (output is { Active: true, CanSwitch: true })
        {
            _audio.SetAirPodsDefault(EDataFlow.Render, false);
        }
        Log.Write($"Output switching: worn={worn}, was {output}");
        _outputWorn = worn;
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
        if (s is null)
        {
            // The AirPods went quiet (in the closed case, or out of range): start fresh at the next sighting.
            _ears = null;
            _outputWorn = null;
            _media.Reset();
        }
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
        _smartMicTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
