using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PodBar;

/// <summary>
/// "Smart microphone": turns the AirPods microphone on when an app starts recording (a call begins, or a game
/// opens voice chat) and off again shortly after the last app stops, so the AirPods stay in stereo quality the
/// rest of the time. Individual apps can be switched off so they never trigger it.
/// </summary>
public sealed class SmartMic(AudioControl audio)
{
    /// <summary>How long recording must have stopped before the AirPods microphone is turned back off.</summary>
    static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    public sealed record Recorder(uint ProcessId, string Process, string Device);

    // "In a call" is a debounced state: it starts with the first recording stream and ends only after
    // IdleDelay without one. Short gaps are normal (a stream pauses briefly when Windows moves it to
    // another microphone) and must not count as a new call.
    bool? _inCall;         // null until the first check, so a recording already in progress is not treated as "just started"
    bool _turnedOnByUs;    // only undo what we did; never override a manual choice
    DateTime? _idleSince;

    string _lastIgnored = "";

    public bool Enabled { get; set; }

    /// <summary>Process names the user switched off. Recording by these apps is ignored; every other app counts.</summary>
    public Func<IEnumerable<string>> IgnoredApps { get; set; } = () => [];

    /// <summary>Every app seen recording since PodBar started, so the menu can offer a switch for each.</summary>
    public SortedSet<string> SeenApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call every second or two from the UI thread.</summary>
    public void Tick()
    {
        if (!Enabled)
        {
            _inCall = null;
            _turnedOnByUs = false;
            _idleSince = null;
            return;
        }

        var all = ActiveRecorders();
        foreach (var r in all) SeenApps.Add(r.Process);

        var off = new HashSet<string>(IgnoredApps(), StringComparer.OrdinalIgnoreCase);
        var recorders = all.Where(r => !off.Contains(r.Process)).ToList();

        string ignored = string.Join(", ", all.Where(r => off.Contains(r.Process)).Select(r => r.Process).Distinct());
        if (ignored != _lastIgnored)
        {
            _lastIgnored = ignored;
            if (ignored != "") Log.Write($"Smart mic: ignoring recording by {ignored} (switched off in the menu)");
        }

        if (recorders.Count > 0)
        {
            _idleSince = null;
            bool callStarted = _inCall == false;
            _inCall = true;
            if (!callStarted) return; // act once per call; if the user switches the mic off mid-call, leave it off

            var mic = audio.GetStatus(EDataFlow.Capture);
            Log.Write($"Smart mic: call started ({string.Join(", ", recorders.Select(r => r.Process).Distinct())} recording); AirPods mic {mic}");
            if (mic is { Present: true, Active: false } && audio.SetAirPodsDefault(EDataFlow.Capture, true))
                _turnedOnByUs = true;
            return;
        }

        if (_inCall != true)
        {
            _inCall = false;
            return;
        }

        _idleSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _idleSince < IdleDelay) return;

        _inCall = false;
        _idleSince = null;
        if (!_turnedOnByUs) return;

        _turnedOnByUs = false;
        var after = audio.GetStatus(EDataFlow.Capture);
        Log.Write($"Smart mic: call ended; AirPods mic {after}");
        if (after is { Active: true, CanSwitch: true }) audio.SetAirPodsDefault(EDataFlow.Capture, false);
    }

    /// <summary>Apps that currently have a running recording stream on any microphone.</summary>
    public static List<Recorder> ActiveRecorders()
    {
        var result = new List<Recorder>();
        try
        {
            const int AudioSessionStateActive = 1;
            uint self = (uint)Environment.ProcessId;

            foreach (var device in AudioControl.Enumerate(EDataFlow.Capture, DeviceState.Active))
            {
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object o) != 0) continue;
                if (((IAudioSessionManager2)o).GetSessionEnumerator(out var sessions) != 0) continue;
                if (sessions.GetCount(out int count) != 0) continue;

                string deviceName = AudioControl.FriendlyName(device);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0) continue;
                    if (session.GetState(out int state) != 0 || state != AudioSessionStateActive) continue;
                    if (session.GetProcessId(out uint pid) != 0 || pid == 0 || pid == self) continue;
                    result.Add(new Recorder(pid, ProcessName(pid), deviceName));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Listing recording apps failed: {ex.Message}");
        }
        return result;
    }

    static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {pid}";
        }
    }
}

// ---- Core Audio session interop (only the members this app calls are given real signatures) ----

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    // Order must match the native vtable exactly. The first two belong to the base IAudioSessionManager.
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // Order must match the native vtable exactly. The first nine belong to the base IAudioSessionControl.
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(IntPtr groupingId, IntPtr context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint processId);
}
