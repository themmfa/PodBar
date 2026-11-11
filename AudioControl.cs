using System.Runtime.InteropServices;

namespace PodBar;

/// <summary>
/// Volume control and "use the AirPods / use something else" switching through the Windows Core Audio API.
/// AirPods devices are recognised by "AirPods" in the Windows device name.
/// </summary>
public sealed class AudioControl
{
    sealed record Endpoint(string Name, IAudioEndpointVolume Volume);

    /// <summary>
    /// Where the AirPods stand as a playback or recording device.
    /// For the microphone, "Active" means apps pick it up (and Windows drops the AirPods into headset mode while it is in use).
    /// </summary>
    /// <param name="Present">Windows lists a connected AirPods device of this kind.</param>
    /// <param name="Active">The AirPods are the Windows default device of this kind.</param>
    /// <param name="CurrentName">Name of the device Windows uses right now.</param>
    /// <param name="CanSwitch">Toggling is possible (to activate: AirPods present; to deactivate: another device exists).</param>
    public sealed record DefaultStatus(bool Present, bool Active, string? CurrentName, bool CanSwitch);

    // The device that was default before we switched to the AirPods, per direction.
    readonly Dictionary<EDataFlow, string> _previous = new();

    /// <summary>Playback volume 0..1, or null when there is no playback device.</summary>
    public (string Name, float Level)? GetVolume()
    {
        var ep = FindVolumeEndpoint();
        if (ep is null || ep.Volume.GetMasterVolumeLevelScalar(out float level) != 0) return null;
        return (ep.Name, level);
    }

    public void SetVolume(float level)
    {
        var ep = FindVolumeEndpoint();
        if (ep is null) return;
        Guid context = Guid.Empty;
        ep.Volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref context);
        if (level > 0) ep.Volume.SetMute(false, ref context);
    }

    public DefaultStatus GetStatus(EDataFlow flow)
    {
        try
        {
            var devices = ActiveDevices(flow);
            var current = DefaultDevice(flow);

            // Remember the device in use whenever it is not the AirPods, so "switch away from the AirPods" can
            // return to it. Without this, a default that Windows changed by itself left nothing to return to
            // and the fallback picked an arbitrary device (for example a monitor's speakers).
            if (current is { IsAirPods: false }) _previous[flow] = current.Id;

            bool present = devices.Any(d => d.IsAirPods);
            bool active = current?.IsAirPods == true;
            bool canSwitch = active ? devices.Any(d => !d.IsAirPods) : present;
            return new DefaultStatus(present, active, current?.Name, canSwitch);
        }
        catch (Exception ex)
        {
            Log.Write($"Device status failed: {ex.Message}");
            return new DefaultStatus(false, false, null, false);
        }
    }

    /// <summary>Makes the AirPods the default device for <paramref name="flow"/>, or hands the default to another device.</summary>
    public bool SetAirPodsDefault(EDataFlow flow, bool useAirPods)
    {
        try
        {
            var devices = ActiveDevices(flow);
            var current = DefaultDevice(flow);

            DeviceInfo? target;
            if (useAirPods)
            {
                target = devices.FirstOrDefault(d => d.IsAirPods);
                if (current is { IsAirPods: false }) _previous[flow] = current.Id;
            }
            else
            {
                _previous.TryGetValue(flow, out string? previousId);
                target = devices.FirstOrDefault(d => !d.IsAirPods && d.Id == previousId)
                      ?? devices.FirstOrDefault(d => !d.IsAirPods);
            }
            if (target is null) return false;

            // IPolicyConfig is the (undocumented but long-stable) interface the Windows sound settings use.
            var policy = (IPolicyConfig)new PolicyConfigClient();
            int hr = 0;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                hr |= policy.SetDefaultEndpoint(target.Id, role);
            Log.Write($"Default {flow} device -> {target.Name} (hr=0x{hr:X8})");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Switching {flow} device failed: {ex.Message}");
            return false;
        }
    }

    // ---- Helpers shared with AirPodsConnection ----

    internal sealed record DeviceInfo(string Id, string Name)
    {
        public bool IsAirPods => AudioControl.IsAirPodsName(Name);
    }

    internal static bool IsAirPodsName(string name) => name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);

    static DeviceInfo? Describe(IMMDevice device) =>
        device.GetId(out string id) == 0 ? new DeviceInfo(id, FriendlyName(device)) : null;

    static DeviceInfo? DefaultDevice(EDataFlow flow)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var role = flow == EDataFlow.Capture ? ERole.Communications : ERole.Console;
        return enumerator.GetDefaultAudioEndpoint(flow, role, out var device) == 0 ? Describe(device) : null;
    }

    static List<DeviceInfo> ActiveDevices(EDataFlow flow)
    {
        var result = new List<DeviceInfo>();
        foreach (var device in Enumerate(flow, DeviceState.Active))
            if (Describe(device) is { } info)
                result.Add(info);
        return result;
    }

    internal static IEnumerable<IMMDevice> Enumerate(EDataFlow flow, int stateMask)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        if (enumerator.EnumAudioEndpoints(flow, stateMask, out var all) != 0 || all.GetCount(out int count) != 0)
            yield break;
        for (int i = 0; i < count; i++)
            if (all.Item(i, out var device) == 0)
                yield return device;
    }

    /// <summary>The AirPods playback endpoint when connected (preferring it if it is the default), else the default device.</summary>
    static Endpoint? FindVolumeEndpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

            IMMDevice? fallback = null;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var def) == 0)
            {
                if (IsAirPodsName(FriendlyName(def))) return Open(def);
                fallback = def;
            }

            foreach (var device in Enumerate(EDataFlow.Render, DeviceState.Active))
                if (IsAirPodsName(FriendlyName(device)))
                    return Open(device);

            return fallback is null ? null : Open(fallback);
        }
        catch (Exception ex)
        {
            Log.Write($"Audio endpoint lookup failed: {ex.Message}");
            return null;
        }
    }

    static Endpoint? Open(IMMDevice device)
    {
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object o) != 0) return null;
        return new Endpoint(FriendlyName(device), (IAudioEndpointVolume)o);
    }

    internal static string FriendlyName(IMMDevice device)
    {
        const int StgmRead = 0;
        if (device.OpenPropertyStore(StgmRead, out var store) != 0) return "";
        var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
        if (store.GetValue(ref key, out var value) != 0) return "";
        try
        {
            const ushort VtLpwstr = 31;
            return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);
}

public enum AirPodsLink { NotPaired, Disconnected, Connected }

/// <summary>
/// Connects and disconnects the AirPods' Bluetooth audio the same way the Windows sound flyout does:
/// by sending the "reconnect" / "disconnect" one-shot property to the Bluetooth audio driver behind each endpoint.
/// </summary>
public static class AirPodsConnection
{
    static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint OneShotReconnect = 0, OneShotDisconnect = 1, KsPropertyTypeGet = 1;

    public static AirPodsLink GetLink()
    {
        try
        {
            var link = AirPodsLink.NotPaired;
            foreach (var device in AudioControl.Enumerate(EDataFlow.All, DeviceState.Active | DeviceState.Unplugged))
            {
                if (!AudioControl.IsAirPodsName(AudioControl.FriendlyName(device))) continue;
                if (device.GetState(out int state) == 0 && state == DeviceState.Active) return AirPodsLink.Connected;
                link = AirPodsLink.Disconnected;
            }
            return link;
        }
        catch (Exception ex)
        {
            Log.Write($"Reading connection state failed: {ex.Message}");
            return AirPodsLink.NotPaired;
        }
    }

    /// <summary>Asks the Bluetooth audio driver to connect or disconnect. Returns how many driver endpoints accepted.</summary>
    public static int SetLink(bool connect, bool dryRun = false)
    {
        int accepted = 0;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var endpoint in AudioControl.Enumerate(EDataFlow.All, DeviceState.Active | DeviceState.Unplugged))
            {
                if (!AudioControl.IsAirPodsName(AudioControl.FriendlyName(endpoint))) continue;

                // Endpoint -> its topology -> the driver filter it is wired to -> that filter's control interface.
                Guid topologyIid = typeof(IDeviceTopology).GUID;
                if (endpoint.Activate(ref topologyIid, ClsCtx.All, IntPtr.Zero, out object t) != 0) continue;
                if (((IDeviceTopology)t).GetConnector(0, out var connector) != 0) continue;
                if (connector.GetDeviceIdConnectedTo(out string filterId) != 0 || !seen.Add(filterId)) continue;
                if (enumerator.GetDevice(filterId, out var filter) != 0) continue;

                Guid ksIid = typeof(IKsControl).GUID;
                if (filter.Activate(ref ksIid, ClsCtx.All, IntPtr.Zero, out object k) != 0) continue;
                if (dryRun) { accepted++; continue; }

                var property = new KsProperty
                {
                    Set = KsPropSetBtAudio,
                    Id = connect ? OneShotReconnect : OneShotDisconnect,
                    Flags = KsPropertyTypeGet,
                };
                int hr = ((IKsControl)k).KsProperty(ref property, Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                Log.Write($"{(connect ? "Connect" : "Disconnect")} -> hr=0x{hr:X8}");
                if (hr == 0) accepted++;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Bluetooth {(connect ? "connect" : "disconnect")} failed: {ex.Message}");
        }
        return accepted;
    }
}

// ---- Core Audio COM interop (only the members this app calls are given real signatures) ----

public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

static class DeviceState { public const int Active = 1, Unplugged = 8; }
static class ClsCtx { public const int All = 23; }

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey { public Guid FormatId; public int PropertyId; }

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort Type;
    ushort _r1, _r2, _r3;
    public IntPtr Pointer;
    IntPtr _extra;
}

[StructLayout(LayoutKind.Sequential)]
struct KsProperty { public Guid Set; public uint Id; public uint Flags; }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    // Order must match the native vtable exactly.
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out int count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology
{
    [PreserveSig] int GetConnectorCount(out uint count);
    [PreserveSig] int GetConnector(uint index, out IConnector connector);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector
{
    // Order must match the native vtable exactly.
    [PreserveSig] int GetConnectorType(out int type);
    [PreserveSig] int GetDataFlow(out int flow);
    [PreserveSig] int ConnectTo(IConnector other);
    [PreserveSig] int Disconnect();
    [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    [PreserveSig] int GetConnectedTo(out IConnector other);
    [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IKsControl
{
    [PreserveSig] int KsProperty(ref KsProperty property, int propertyLength, IntPtr data, int dataLength, out int bytesReturned);
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
class PolicyConfigClient { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    // Ten vtable slots we never call; they only keep SetDefaultEndpoint at the right position.
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
}
