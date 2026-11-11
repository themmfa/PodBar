using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PodBar;

/// <summary>
/// Borderless card above the tray. Two views share the window:
/// the main view (battery rings, now playing, volume, AirPods microphone switch) and the scrollable settings view.
/// </summary>
public sealed class PopupForm : Form
{
    enum View { Main, Settings }

    // ---- Settings rows ----
    abstract record Row;
    sealed record HeaderRow(string Text) : Row;
    sealed record ToggleRow(string Title, Func<string> Description, Func<bool> Get, Action<bool> Set) : Row;
    sealed record ShortcutRow(string Title, Func<int> Get, Action<int> Set) : Row;
    sealed record ChoiceRow(string Title, string Description, Func<string> Value, Action Next) : Row; // click cycles the options

    // ---- Colours ----
    sealed record Theme(
        Color BgTop, Color BgBottom, Color Surface, Color Track, Color Text, Color Dim,
        Color BarFill, Color OnBarFill, Color Accent, Color Good, int BorderColorRef)
    {
        static readonly Color Amber = Color.FromArgb(255, 170, 51), Red = Color.FromArgb(255, 69, 58);

        public Color Battery(int percent) => percent <= 10 ? Red : percent <= 20 ? Amber : Good;

        public static Theme Dark(Color accent) => new(
            Color.FromArgb(44, 44, 52), Color.FromArgb(24, 24, 29), Color.FromArgb(120, 58, 58, 68), Color.FromArgb(70, 70, 82),
            Color.FromArgb(245, 245, 250), Color.FromArgb(152, 152, 166),
            Color.FromArgb(236, 236, 242), Color.FromArgb(30, 30, 36), accent, Color.FromArgb(48, 209, 88), 0x00504646);

        public static Theme Light(Color accent) => new(
            Color.FromArgb(252, 252, 254), Color.FromArgb(232, 232, 240), Color.FromArgb(215, 255, 255, 255), Color.FromArgb(204, 204, 216),
            Color.FromArgb(26, 26, 32), Color.FromArgb(104, 104, 118),
            Color.FromArgb(40, 40, 48), Color.White, accent, Color.FromArgb(36, 174, 74), 0x00D6CECE);

        /// <summary>Reads the Windows app theme and accent colour.</summary>
        public static Theme FromWindows(bool followTheme)
        {
            Color accent = Color.FromArgb(10, 132, 255);
            bool light = false;
            try
            {
                using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (dwm?.GetValue("AccentColor") is int abgr)
                    accent = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);

                using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                light = followTheme && personalize?.GetValue("AppsUseLightTheme") is 1;
            }
            catch { /* fall back to dark with the default accent */ }
            return light ? Light(accent) : Dark(accent);
        }
    }

    // ---- Layout, in 96-DPI units (scaled when painting and hit-testing) ----
    const float W = 340, H = 388, Pad = 16, Inner = W - 2 * Pad, SettingsTop = 54;
    static readonly RectangleF GearHit = new(W - 46, 10, 34, 34);
    static readonly RectangleF BackHit = new(8, 10, 34, 34);
    static readonly RectangleF LinkButton = new(W - 46 - 92, 14, 88, 26);
    static readonly RectangleF NowRow = new(Pad, 186, Inner, 56);
    static readonly RectangleF PrevHit = new(NowRow.Right - 102, NowRow.Y + 12, 30, 32);
    static readonly RectangleF PlayHit = new(NowRow.Right - 70, NowRow.Y + 12, 30, 32);
    static readonly RectangleF NextHit = new(NowRow.Right - 38, NowRow.Y + 12, 30, 32);
    static readonly RectangleF VolumeBar = new(Pad, 272, Inner, 36);
    static readonly RectangleF MicRow = new(Pad, 320, Inner, 52);

    // Segoe Fluent Icons glyphs (ships with Windows 11).
    const string IconFont = "Segoe Fluent Icons";
    const string GlyphSettings = "", GlyphBack = "", GlyphVolume = "", GlyphMuted = "", GlyphMic = "",
                 GlyphPrev = "", GlyphNext = "", GlyphPlay = "", GlyphPause = "", GlyphMusic = "";

    const int WmHotkey = 0x0312;
    const int HotkeyMic = 1, HotkeyVolumeUp = 2, HotkeyVolumeDown = 3, HotkeyShowCard = 4;
    const int AnimationMs = 380;

    readonly AppSettings _settings;
    readonly AudioControl _audio;
    readonly NowPlaying _now = new();
    readonly System.Windows.Forms.Timer _autoHide = new();
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
    readonly Stopwatch _animClock = new();
    readonly Row[] _rows;
    readonly HashSet<int> _registeredHotkeys = [];

    Theme _theme = Theme.Dark(Color.FromArgb(10, 132, 255));
    View _view = View.Main;
    AirPodsState? _state;
    string _message = "Searching for AirPods…";
    (string Name, float Level)? _volume;
    AudioControl.DefaultStatus? _mic;
    AirPodsLink _link = AirPodsLink.NotPaired;
    string? _linkBusy;          // "Connecting…" while we wait for Windows to finish
    DateTime _linkBusyUntil;
    TrackInfo? _track;
    bool _dragging, _refreshingTrack;
    float _anim = 1f;           // 0..1 progress of the open animation
    Point _restingLocation;
    float _scroll;              // settings view scroll offset
    ShortcutRow? _capturing;    // shortcut row waiting for a key press

    public PopupForm(AppSettings settings, AudioControl audio)
    {
        _settings = settings;
        _audio = audio;
        _rows =
        [
            new HeaderRow("GENERAL"),
            new ToggleRow("Start with Windows", () => "Launch automatically when you sign in",
                () => settings.StartWithWindows, v => settings.StartWithWindows = v),
            new ToggleRow("Show card on connect", () => "Pop up when your AirPods are detected",
                () => settings.ShowCardOnConnect, v => settings.ShowCardOnConnect = v),
            new ToggleRow("Follow Windows theme", () => "Match Windows light or dark mode",
                () => settings.FollowWindowsTheme, v => { settings.FollowWindowsTheme = v; ApplyTheme(); }),
            new ToggleRow("Low battery alert", () => "Notify when a pod drops to 20%",
                () => settings.LowBatteryAlert, v => settings.LowBatteryAlert = v),
            new ToggleRow("Only my AirPods",
                () => settings.LockedModel != "" ? $"Ignoring everything except {settings.LockedModel}"
                    : _state is null ? "Bring your AirPods nearby to turn this on"
                    : $"Ignore other models; lock to {_state.Model}",
                () => settings.LockedModel != "",
                v => settings.LockedModel = v ? _state?.Model ?? "" : ""),

            new HeaderRow("PLAYBACK"),
            new ChoiceRow("Pause media when", "Click to change",
                () => settings.PauseWhen switch
                {
                    PauseWhen.OnePodOut => "One pod is out",
                    PauseWhen.BothPodsOut => "Both pods are out",
                    _ => "Never",
                },
                () => settings.PauseWhen = settings.PauseWhen switch
                {
                    PauseWhen.OnePodOut => PauseWhen.BothPodsOut,
                    PauseWhen.BothPodsOut => PauseWhen.Never,
                    _ => PauseWhen.OnePodOut,
                }),
            new ToggleRow("Automatic output switching", () => "AirPods when in your ears, speakers when out",
                () => settings.AutoOutputSwitch, v => settings.AutoOutputSwitch = v),

            new HeaderRow("SHORTCUTS  ·  CLICK, THEN PRESS KEYS  ·  BACKSPACE CLEARS"),
            new ShortcutRow("AirPods microphone on/off", () => settings.MicHotkey, v => settings.MicHotkey = v),
            new ShortcutRow("Volume up", () => settings.VolumeUpHotkey, v => settings.VolumeUpHotkey = v),
            new ShortcutRow("Volume down", () => settings.VolumeDownHotkey, v => settings.VolumeDownHotkey = v),
            new ShortcutRow("Show this card", () => settings.ShowCardHotkey, v => settings.ShowCardHotkey = v),
        ];

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        _autoHide.Tick += (_, _) => { _autoHide.Stop(); Hide(); };
        _refresh.Tick += (_, _) => { if (Visible && !_dragging) RefreshAll(); };
        _animTimer.Tick += (_, _) => StepAnimation();
        Deactivate += (_, _) => Hide();
        VisibleChanged += (_, _) =>
        {
            if (Visible) { _refresh.Start(); return; }
            _refresh.Stop();
            _animTimer.Stop();
            _dragging = false;
            _capturing = null;
            _view = View.Main;
            _scroll = 0;
        };
        settings.Changed += ApplyHotkeys;
        ApplyTheme();
    }

    public AudioControl.DefaultStatus MicStatus => _audio.GetStatus(EDataFlow.Capture);

    // Lets the card appear on its own without stealing keyboard focus.
    protected override bool ShowWithoutActivation => true;

    // ---- Window plumbing ----

    void ApplyTheme()
    {
        _theme = Theme.FromWindows(_settings.FollowWindowsTheme);
        BackColor = _theme.BgBottom;
        if (IsHandleCreated)
        {
            int border = _theme.BorderColorRef; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND; Windows 11 only, ignored elsewhere
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        ApplyTheme();
        ApplyHotkeys();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (int id in _registeredHotkeys) UnregisterHotKey(Handle, id);
        _registeredHotkeys.Clear();
        base.OnHandleDestroyed(e);
    }

    /// <summary>Re-registers the global shortcuts to match the settings.</summary>
    void ApplyHotkeys()
    {
        if (!IsHandleCreated) return;
        foreach (int id in _registeredHotkeys) UnregisterHotKey(Handle, id);
        _registeredHotkeys.Clear();

        Register(HotkeyMic, _settings.MicHotkey, repeat: false);
        Register(HotkeyVolumeUp, _settings.VolumeUpHotkey, repeat: true);
        Register(HotkeyVolumeDown, _settings.VolumeDownHotkey, repeat: true);
        Register(HotkeyShowCard, _settings.ShowCardHotkey, repeat: false);

        void Register(int id, int value, bool repeat)
        {
            if (value == 0) return;
            var keys = (Keys)value;
            uint modifiers = repeat ? 0u : 0x4000u; // MOD_NOREPEAT
            if (keys.HasFlag(Keys.Alt)) modifiers |= 0x1;
            if (keys.HasFlag(Keys.Control)) modifiers |= 0x2;
            if (keys.HasFlag(Keys.Shift)) modifiers |= 0x4;

            if (RegisterHotKey(Handle, id, modifiers, (uint)(keys & Keys.KeyCode))) _registeredHotkeys.Add(id);
            else Log.Write($"Could not register {AppSettings.DescribeHotkey(value)} (already used by another app)");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            switch ((int)m.WParam)
            {
                case HotkeyMic:
                    ToggleMic();
                    Flash(2000);
                    break;
                case HotkeyVolumeUp or HotkeyVolumeDown:
                    RefreshAudio();
                    if (_volume is var (_, level)) SetVolume(level + ((int)m.WParam == HotkeyVolumeUp ? 0.04f : -0.04f));
                    Flash(1500);
                    break;
                case HotkeyShowCard:
                    if (Visible) Hide(); else ShowCard();
                    break;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>Shows the card briefly as on-screen confirmation, without taking focus.</summary>
    void Flash(int ms)
    {
        if (!Visible) { ShowCard(autoHideMs: ms); return; }
        if (!_autoHide.Enabled) return; // the user opened it themselves; leave it alone
        _autoHide.Stop();
        _autoHide.Interval = ms;
        _autoHide.Start();
    }

    // ---- State ----

    public void SetState(AirPodsState? state, string message)
    {
        _state = state;
        _message = message;
        if (Visible) Invalidate();
    }

    public void ToggleMic()
    {
        var mic = _audio.GetStatus(EDataFlow.Capture);
        if (!mic.CanSwitch) return;
        _audio.SetAirPodsDefault(EDataFlow.Capture, !mic.Active);
        RefreshAudio();
    }

    public AirPodsLink Link => AirPodsConnection.GetLink();

    /// <summary>Connects the AirPods if they are disconnected, disconnects them if connected.</summary>
    public void ToggleLink()
    {
        var link = AirPodsConnection.GetLink();
        if (link == AirPodsLink.NotPaired) return;
        bool connect = link != AirPodsLink.Connected;
        _linkBusy = connect ? "Connecting…" : "Disconnecting…";
        _linkBusyUntil = DateTime.UtcNow.AddSeconds(12);
        Task.Run(() => AirPodsConnection.SetLink(connect));
        Invalidate();
    }

    void RefreshAll()
    {
        RefreshAudio();
        _ = RefreshTrackAsync();
    }

    void RefreshAudio()
    {
        var volume = _audio.GetVolume();
        var mic = _audio.GetStatus(EDataFlow.Capture);
        var link = AirPodsConnection.GetLink();

        bool busyEnded = _linkBusy is not null && (link != _link || DateTime.UtcNow > _linkBusyUntil);
        if (busyEnded) _linkBusy = null;
        if (volume == _volume && mic == _mic && link == _link && !busyEnded) return;

        _volume = volume;
        _mic = mic;
        _link = link;
        Invalidate();
    }

    async Task RefreshTrackAsync()
    {
        if (_refreshingTrack) return;
        _refreshingTrack = true;
        try
        {
            var before = _now.Art;
            var track = await _now.GetAsync();
            if (track != _track || !ReferenceEquals(before, _now.Art))
            {
                _track = track;
                if (Visible) Invalidate();
            }
        }
        finally
        {
            _refreshingTrack = false;
        }
    }

    async void SendMedia(MediaCommand command)
    {
        await _now.SendAsync(command);
        await Task.Delay(250); // give the player a moment to report its new state
        await RefreshTrackAsync();
    }

    /// <summary>Shows the card. With <paramref name="autoHideMs"/> it appears passively and closes itself.</summary>
    public void ShowCard(int? autoHideMs = null, bool settingsView = false)
    {
        ApplyTheme();
        float s = DeviceDpi / 96f;
        Size = new Size((int)(W * s), (int)(H * s));
        var area = Screen.PrimaryScreen!.WorkingArea;
        _restingLocation = new Point(area.Right - Width - (int)(12 * s), area.Bottom - Height - (int)(12 * s));

        _view = settingsView ? View.Settings : View.Main;
        RefreshAll();

        // Open animation: fade and slide up while the battery rings fill.
        _anim = 0f;
        Opacity = 0;
        Location = _restingLocation with { Y = _restingLocation.Y + (int)(14 * s) };
        _animClock.Restart();
        _animTimer.Start();

        _autoHide.Stop();
        Show();
        if (autoHideMs is int ms)
        {
            _autoHide.Interval = ms;
            _autoHide.Start();
        }
        else
        {
            Activate();
        }
        Invalidate();
    }

    void StepAnimation()
    {
        _anim = Math.Min(1f, _animClock.ElapsedMilliseconds / (float)AnimationMs);
        float eased = Ease(_anim);
        Opacity = Math.Min(1.0, _anim * 3.0);
        Location = _restingLocation with { Y = _restingLocation.Y + (int)((1 - eased) * 14 * DeviceDpi / 96f) };
        if (_anim >= 1f) _animTimer.Stop();
        Invalidate();
    }

    static float Ease(float t) => 1 - MathF.Pow(1 - t, 3); // ease-out cubic

    // ---- Settings layout ----

    /// <summary>Row rectangles in content coordinates (before scrolling), and the total content height.</summary>
    (List<(Row Row, RectangleF Rect)> Items, float Height) LayoutRows()
    {
        var items = new List<(Row, RectangleF)>();
        float y = 0;
        foreach (var row in _rows)
        {
            float height = row is HeaderRow ? 26 : 48;
            items.Add((row, new RectangleF(Pad, y, Inner, height)));
            y += height + (row is HeaderRow ? 0 : 4);
        }
        return (items, y + 8);
    }

    float MaxScroll => Math.Max(0, LayoutRows().Height - (H - SettingsTop));

    // ---- Mouse and keyboard ----

    PointF ToCard(Point p) => new(p.X * 96f / DeviceDpi, p.Y * 96f / DeviceDpi);

    void SetVolume(float level)
    {
        if (_volume is null) return;
        level = Math.Clamp(level, 0f, 1f);
        _audio.SetVolume(level);
        _volume = (_volume.Value.Name, level);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _autoHide.Stop(); // the user is interacting; stay open until they click away
        if (e.Button != MouseButtons.Left) return;
        var p = ToCard(e.Location);

        if (_view == View.Settings)
        {
            ClickSettings(p);
            Invalidate();
            return;
        }

        if (GearHit.Contains(p)) { _view = View.Settings; Invalidate(); }
        else if (_link != AirPodsLink.NotPaired && LinkButton.Contains(p)) { if (_linkBusy is null) ToggleLink(); }
        else if (_track is not null && PrevHit.Contains(p)) SendMedia(MediaCommand.Previous);
        else if (_track is not null && PlayHit.Contains(p)) SendMedia(MediaCommand.PlayPause);
        else if (_track is not null && NextHit.Contains(p)) SendMedia(MediaCommand.Next);
        else if (VolumeBar.Contains(p)) { _dragging = true; SetVolume((p.X - VolumeBar.X) / VolumeBar.Width); }
        else if (MicRow.Contains(p)) ToggleMic();
    }

    void ClickSettings(PointF p)
    {
        _capturing = null;
        if (BackHit.Contains(p)) { _view = View.Main; _scroll = 0; return; }
        if (p.Y < SettingsTop) return;

        var content = new PointF(p.X, p.Y - SettingsTop + _scroll);
        foreach (var (row, rect) in LayoutRows().Items)
        {
            if (!rect.Contains(content)) continue;
            if (row is ShortcutRow shortcut)
            {
                _capturing = shortcut;
            }
            else if (row is ChoiceRow choice)
            {
                choice.Next();
                _settings.Save();
            }
            else if (row is ToggleRow toggle)
            {
                try { toggle.Set(!toggle.Get()); }
                catch (Exception ex) { Log.Write($"Changing '{toggle.Title}' failed: {ex.Message}"); }
                _settings.Save();
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetVolume((ToCard(e.Location).X - VolumeBar.X) / VolumeBar.Width);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_view == View.Settings)
        {
            _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * 52, 0, MaxScroll);
            Invalidate();
        }
        else if (_volume is var (_, level))
        {
            SetVolume(level + Math.Sign(e.Delta) * 0.02f);
        }
    }

    /// <summary>While a shortcut row is waiting, the next key combination becomes its shortcut.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturing is null) return base.ProcessCmdKey(ref msg, keyData);

        var code = keyData & Keys.KeyCode;
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true; // modifier alone: keep waiting

        if (code == Keys.Escape)
        {
            _capturing = null;
        }
        else if (code is Keys.Back or Keys.Delete)
        {
            _capturing.Set(0);
            _capturing = null;
            _settings.Save();
        }
        else if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) != 0 || code is >= Keys.F1 and <= Keys.F24)
        {
            // A plain letter would hijack typing everywhere, so require a modifier (function keys are fine alone).
            _capturing.Set((int)keyData);
            _capturing = null;
            _settings.Save();
        }
        Invalidate();
        return true;
    }

    // ---- Painting ----

    protected override void OnPaint(PaintEventArgs e) => PaintCard(e.Graphics, DeviceDpi / 96f);

    void PaintCard(Graphics g, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(scale, scale); // everything below is in 96-DPI units

        using (var bg = new LinearGradientBrush(new RectangleF(0, -1, W, H + 2), _theme.BgTop, _theme.BgBottom, 90f))
            g.FillRectangle(bg, -1, -1, W + 2, H + 2);

        using var p = new Paints(_theme);
        if (_view == View.Settings) PaintSettings(g, p); else PaintMain(g, p);
    }

    void PaintMain(Graphics g, Paints p)
    {
        // Header: model, status line, connect button, settings gear
        bool showLink = _link != AirPodsLink.NotPaired;
        float titleRight = showLink ? LinkButton.X - 6 : GearHit.X;
        g.DrawString(_state?.Model ?? "AirPods", p.Title, p.Main, new RectangleF(Pad - 2, 13, titleRight - Pad + 2, 24), p.Ellipsis);

        string ears = _state switch
        {
            null => "",
            { LeftInEar: true, RightInEar: true } => "Both in ear",
            { LeftInEar: true } => "Left in ear",
            { RightInEar: true } => "Right in ear",
            _ => "Not in ear",
        };
        string subtitle = (_link, _state) switch
        {
            (AirPodsLink.Connected, null) => "Connected · waiting for battery info",
            (AirPodsLink.Connected, _) => $"Connected · {ears}",
            (AirPodsLink.Disconnected, not null) => $"Not connected · {ears}",
            (_, null) => _message,
            _ => ears,
        };
        g.DrawString(subtitle, p.Small, p.Dim, new RectangleF(Pad, 37, Inner - 40, 16), p.Ellipsis);
        g.DrawString(GlyphSettings, p.Icon, p.Dim, GearHit, p.Center);

        if (showLink)
        {
            bool connected = _link == AirPodsLink.Connected;
            using var fill = new SolidBrush(connected || _linkBusy is not null ? _theme.Track : _theme.Accent);
            using (var path = Rounded(LinkButton, LinkButton.Height / 2)) g.FillPath(fill, path);
            string text = _linkBusy ?? (connected ? "Disconnect" : "Connect");
            using var textBrush = new SolidBrush(_linkBusy is not null ? _theme.Dim : connected ? _theme.Text : Color.White);
            g.DrawString(text, p.Small, textBrush, LinkButton, p.Center);
        }

        // Battery rings
        float third = Inner / 3;
        DrawRing(g, p, Pad + third * 0.5f, "Left", _state?.Left, _state?.LeftCharging == true, _state?.LeftInEar == true);
        DrawRing(g, p, Pad + third * 1.5f, "Right", _state?.Right, _state?.RightCharging == true, _state?.RightInEar == true);
        DrawRing(g, p, Pad + third * 2.5f, "Case", _state?.Case, _state?.CaseCharging == true, false);

        PaintNowPlaying(g, p);

        // Volume
        g.DrawString("VOLUME", p.Caption, p.Dim, Pad, 252);
        if (_volume is not null)
            g.DrawString(_volume.Value.Name, p.Caption, p.Dim, new RectangleF(Pad + 70, 252, Inner - 70, 14), p.RightEllipsis);

        float level = _volume?.Level ?? 0f;
        using (var bar = Rounded(VolumeBar, 12))
        {
            g.FillPath(p.TrackBrush, bar);
            var saved = g.Save();
            g.SetClip(bar, CombineMode.Intersect);
            g.FillRectangle(p.BarFill, VolumeBar.X, VolumeBar.Y, VolumeBar.Width * level, VolumeBar.Height);
            g.Restore(saved);
        }
        var glyphBox = new RectangleF(VolumeBar.X + 6, VolumeBar.Y, 30, VolumeBar.Height);
        g.DrawString(level <= 0.001f ? GlyphMuted : GlyphVolume, p.Icon, level > 0.09f ? p.OnBarFill : p.Main, glyphBox, p.Center);
        string volumeText = _volume is null ? "No device" : $"{(int)Math.Round(level * 100)}%";
        var textBox = new RectangleF(VolumeBar.X, VolumeBar.Y, VolumeBar.Width - 12, VolumeBar.Height);
        g.DrawString(volumeText, p.Body, level > 0.9f ? p.OnBarFill : p.Main, textBox, p.RightMiddle);

        // AirPods microphone switch
        using (var row = Rounded(MicRow, 12)) g.FillPath(p.SurfaceBrush, row);
        g.DrawString(GlyphMic, p.Icon, p.Main, new RectangleF(MicRow.X + 6, MicRow.Y, 34, MicRow.Height), p.Center);
        g.DrawString("AirPods microphone", p.Body, p.Main, MicRow.X + 42, MicRow.Y + 8);
        string micText = _mic switch
        {
            null or { Present: false } => "Not connected",
            { Active: true, CanSwitch: false } => "On · no other microphone to switch to",
            { Active: true } => "On · apps will use it",
            { CurrentName: string name } => $"Off · using {name}",
            _ => "Off",
        };
        g.DrawString(micText, p.Small, p.Dim, new RectangleF(MicRow.X + 43, MicRow.Y + 27, MicRow.Width - 43 - 62, 16), p.Ellipsis);
        DrawSwitch(g, MicRow, _mic?.Active == true, _mic?.CanSwitch == true);
    }

    void PaintNowPlaying(Graphics g, Paints p)
    {
        using (var row = Rounded(NowRow, 12)) g.FillPath(p.SurfaceBrush, row);

        var artBox = new RectangleF(NowRow.X + 8, NowRow.Y + 8, 40, 40);
        using (var artPath = Rounded(artBox, 8))
        {
            if (_track is not null && _now.Art is { } art)
            {
                var saved = g.Save();
                g.SetClip(artPath, CombineMode.Intersect);
                // Centre-crop to a square so wide thumbnails are not squashed.
                float side = Math.Min(art.Width, art.Height);
                var source = new RectangleF((art.Width - side) / 2, (art.Height - side) / 2, side, side);
                g.DrawImage(art, artBox, source, GraphicsUnit.Pixel);
                g.Restore(saved);
            }
            else
            {
                g.FillPath(p.TrackBrush, artPath);
                g.DrawString(GlyphMusic, p.Icon, p.Dim, artBox, p.Center);
            }
        }

        if (_track is null)
        {
            g.DrawString("Nothing playing", p.Body, p.Dim, new RectangleF(artBox.Right + 9, NowRow.Y, 200, NowRow.Height), p.LeftMiddle);
            return;
        }

        float textWidth = PrevHit.X - artBox.Right - 12;
        g.DrawString(_track.Title, p.Body, p.Main, new RectangleF(artBox.Right + 9, NowRow.Y + 10, textWidth, 18), p.Ellipsis);
        g.DrawString(_track.Artist, p.Small, p.Dim, new RectangleF(artBox.Right + 10, NowRow.Y + 29, textWidth, 16), p.Ellipsis);

        g.DrawString(GlyphPrev, p.IconSmall, p.Main, PrevHit, p.Center);
        g.DrawString(_track.Playing ? GlyphPause : GlyphPlay, p.Icon, p.Main, PlayHit, p.Center);
        g.DrawString(GlyphNext, p.IconSmall, p.Main, NextHit, p.Center);
    }

    void DrawRing(Graphics g, Paints p, float cx, string label, int? percent, bool charging, bool inEar)
    {
        const float cy = 100, radius = 29, stroke = 6;
        var box = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);

        using (var trackPen = new Pen(_theme.Track, stroke)) g.DrawEllipse(trackPen, box);
        if (percent is int v && v > 0)
        {
            using var pen = new Pen(_theme.Battery(v), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, box, -90, 360f * v / 100f * Ease(_anim));
        }

        g.DrawString(percent is int n ? $"{n}%" : "–", p.RingValue, percent is null ? p.Dim : p.Main, box, p.Center);
        g.DrawString(label, p.Body, p.Main, new RectangleF(cx - 50, cy + radius + 8, 100, 18), p.Center);

        string? note = charging ? "Charging" : inEar ? "In ear" : null;
        if (note is not null)
        {
            using var noteBrush = new SolidBrush(charging ? _theme.Good : _theme.Dim);
            g.DrawString(note, p.Small, noteBrush, new RectangleF(cx - 50, cy + radius + 25, 100, 16), p.Center);
        }
    }

    void PaintSettings(Graphics g, Paints p)
    {
        g.DrawString(GlyphBack, p.Icon, p.Main, BackHit, p.Center);
        g.DrawString("Settings", p.Title, p.Main, 44, 15);

        var (items, contentHeight) = LayoutRows();
        float viewport = H - SettingsTop;

        var saved = g.Save();
        g.SetClip(new RectangleF(0, SettingsTop, W, viewport));
        g.TranslateTransform(0, SettingsTop - _scroll);

        foreach (var (row, rect) in items)
        {
            if (rect.Bottom < _scroll || rect.Y > _scroll + viewport) continue;

            if (row is HeaderRow header)
            {
                g.DrawString(header.Text, p.Caption, p.Dim, rect.X + 2, rect.Y + 9);
                continue;
            }

            using (var path = Rounded(rect, 12)) g.FillPath(p.SurfaceBrush, path);

            if (row is ToggleRow toggle)
            {
                bool on;
                try { on = toggle.Get(); } catch { on = false; }
                g.DrawString(toggle.Title, p.Body, p.Main, rect.X + 12, rect.Y + 7);
                g.DrawString(toggle.Description(), p.Small, p.Dim, new RectangleF(rect.X + 13, rect.Y + 25, rect.Width - 13 - 62, 16), p.Ellipsis);
                DrawSwitch(g, rect, on, enabled: true);
            }
            else if (row is ChoiceRow choice)
            {
                var pill = new RectangleF(rect.Right - 12 - 122, rect.Y + 11, 122, 26);
                g.DrawString(choice.Title, p.Body, p.Main, rect.X + 12, rect.Y + 7);
                g.DrawString(choice.Description, p.Small, p.Dim, new RectangleF(rect.X + 13, rect.Y + 25, pill.X - rect.X - 18, 16), p.Ellipsis);
                using (var pillPath = Rounded(pill, 8)) g.FillPath(p.TrackBrush, pillPath);
                g.DrawString(choice.Value(), p.Small, p.Main, pill, p.Center);
            }
            else if (row is ShortcutRow shortcut)
            {
                g.DrawString(shortcut.Title, p.Body, p.Main, new RectangleF(rect.X + 12, rect.Y, 180, rect.Height), p.LeftMiddle);

                bool capturing = ReferenceEquals(_capturing, shortcut);
                int value = shortcut.Get();
                var pill = new RectangleF(rect.Right - 12 - 104, rect.Y + 11, 104, 26);
                using (var pillPath = Rounded(pill, 8))
                {
                    g.FillPath(p.TrackBrush, pillPath);
                    if (capturing)
                    {
                        using var outline = new Pen(_theme.Accent, 1.6f);
                        g.DrawPath(outline, pillPath);
                    }
                }
                string text = capturing ? "Press keys…" : AppSettings.DescribeHotkey(value);
                g.DrawString(text, p.Small, capturing || value == 0 ? p.Dim : p.Main, pill, p.Center);
            }
        }
        g.Restore(saved);

        // Scroll indicator
        float max = contentHeight - viewport;
        if (max > 0)
        {
            float thumbHeight = Math.Max(24, viewport * viewport / contentHeight);
            float thumbY = SettingsTop + 4 + (viewport - 8 - thumbHeight) * (_scroll / max);
            using var thumb = Rounded(new RectangleF(W - 7, thumbY, 3, thumbHeight), 1.5f);
            g.FillPath(p.TrackBrush, thumb);
        }
    }

    /// <summary>Draws an on/off switch at the right edge of <paramref name="row"/>.</summary>
    void DrawSwitch(Graphics g, RectangleF row, bool on, bool enabled)
    {
        var body = new RectangleF(row.Right - 12 - 40, row.Y + (row.Height - 22) / 2, 40, 22);
        using var fill = new SolidBrush(on ? (enabled ? _theme.Accent : Color.FromArgb(120, _theme.Accent)) : _theme.Track);
        using (var path = Rounded(body, 11)) g.FillPath(fill, path);
        float knobX = on ? body.Right - 20 : body.X + 2;
        using var knob = new SolidBrush(enabled ? Color.White : Color.FromArgb(170, 255, 255, 255));
        g.FillEllipse(knob, knobX, body.Y + 2, 18, 18);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fonts, brushes and text formats for one paint pass.</summary>
    sealed class Paints(Theme t) : IDisposable
    {
        public readonly Font Title = new("Segoe UI Semibold", 12.5f);
        public readonly Font Body = new("Segoe UI Semibold", 9.5f);
        public readonly Font Small = new("Segoe UI", 8.25f);
        public readonly Font Caption = new("Segoe UI Semibold", 7.5f);
        public readonly Font RingValue = new("Segoe UI Semibold", 11.5f);
        public readonly Font Icon = new(IconFont, 12f);
        public readonly Font IconSmall = new(IconFont, 10f);

        public readonly SolidBrush Main = new(t.Text), Dim = new(t.Dim);
        public readonly SolidBrush TrackBrush = new(t.Track), SurfaceBrush = new(t.Surface);
        public readonly SolidBrush BarFill = new(t.BarFill), OnBarFill = new(t.OnBarFill);

        public readonly StringFormat Center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        public readonly StringFormat LeftMiddle = new() { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        public readonly StringFormat RightMiddle = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        public readonly StringFormat Ellipsis = new() { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        public readonly StringFormat RightEllipsis = new()
        {
            Alignment = StringAlignment.Far,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        public void Dispose()
        {
            foreach (IDisposable d in new IDisposable[]
                     {
                         Title, Body, Small, Caption, RingValue, Icon, IconSmall, Main, Dim, TrackBrush, SurfaceBrush, BarFill, OnBarFill,
                         Center, LeftMiddle, RightMiddle, Ellipsis, RightEllipsis,
                     })
                d.Dispose();
        }
    }

    // ---- Design preview (PodBar.exe --preview <folder>) ----

    /// <summary>Renders the views with sample data to PNG files, without showing a window or touching audio.</summary>
    public static void RenderPreviews(string folder)
    {
        Directory.CreateDirectory(folder);
        using var form = new PopupForm(new AppSettings(), new AudioControl());
        form._state = new AirPodsState("AirPods Pro 2", 80, 20, 60, false, false, true, true, true, -50);
        form._volume = ("Headphones (AirPods Pro)", 0.62f);
        form._mic = new AudioControl.DefaultStatus(true, false, "Microphone Array (Realtek Audio)", true);
        form._link = AirPodsLink.Connected;
        form._track = new TrackInfo("Sample Song Title That Runs Long", "Sample Artist", true);
        var accent = form._theme.Accent;

        Save("main-dark", Theme.Dark(accent), View.Main, 0);
        Save("main-light", Theme.Light(accent), View.Main, 0);
        Save("settings-dark", Theme.Dark(accent), View.Settings, 0);
        Save("settings-light-scrolled", Theme.Light(accent), View.Settings, form.MaxScroll);

        void Save(string name, Theme theme, View view, float scroll)
        {
            form._theme = theme;
            form._view = view;
            form._scroll = scroll;
            const float scale = 2f;
            using var bmp = new Bitmap((int)(W * scale), (int)(H * scale));
            using (var g = Graphics.FromImage(bmp)) form.PaintCard(g, scale);
            bmp.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settings.Changed -= ApplyHotkeys;
            _autoHide.Dispose();
            _refresh.Dispose();
            _animTimer.Dispose();
            _now.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
