using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PodBar;

/// <summary>
/// Listens for BLE advertisements and raises <see cref="StateReceived"/> for nearby AirPods.
/// The Windows watcher stops for good when the Bluetooth radio goes away (Bluetooth switched off,
/// or the adapter re-initialising after sleep), so <see cref="EnsureRunning"/> must be called
/// periodically to bring it back.
/// </summary>
public sealed class AirPodsScanner
{
    /// <summary>Normal cut-off: ignore anything weaker, so a neighbour's AirPods are not picked up.</summary>
    public const short DefaultMinRssi = -70;

    /// <summary>Looser cut-off for when the AirPods are connected to this PC and so known to be the user's.</summary>
    public const short ConnectedMinRssi = -85;

    /// <summary>If the watcher claims to be running but has delivered nothing at all for this long, restart it.</summary>
    static readonly TimeSpan SilenceBeforeRestart = TimeSpan.FromSeconds(90);

    /// <summary>Broadcasts weaker than this (in dBm) are ignored.</summary>
    public volatile short MinRssi = DefaultMinRssi;

    readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    volatile bool _stopRequested;   // the app is exiting
    volatile bool _restarting;      // we stopped the watcher ourselves and will start it again
    volatile string? _failure;      // last reported problem; null while healthy
    long _lastPacketTick = Environment.TickCount64;

    /// <summary>Raised on a background thread.</summary>
    public event Action<AirPodsState>? StateReceived;

    /// <summary>Raised (possibly on a background thread) when scanning is not possible, e.g. Bluetooth is off.</summary>
    public event Action<string>? Failed;

    /// <summary>Raised from <see cref="EnsureRunning"/> when scanning works again after a failure.</summary>
    public event Action? Recovered;

    public AirPodsScanner()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            if (_stopRequested || _restarting) return;
            ReportFailure(e.Error == Windows.Devices.Bluetooth.BluetoothError.Success ? "scanning stopped" : e.Error.ToString());
        };
    }

    public void Start()
    {
        try
        {
            _watcher.Start();
            Interlocked.Exchange(ref _lastPacketTick, Environment.TickCount64);
            if (_failure is null) Log.Write("Watcher started");
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        try { _watcher.Stop(); } catch { /* already stopped */ }
    }

    /// <summary>Call every few seconds: restarts the watcher if it stopped, and reports when it is healthy again.</summary>
    public void EnsureRunning()
    {
        if (_stopRequested) return;

        var status = _watcher.Status;
        if (status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            if (_failure is not null)
            {
                Log.Write("Watcher running again");
                _failure = null;
                Recovered?.Invoke();
            }

            // A watcher can keep saying "started" while delivering nothing after the radio was reset.
            // Any BLE device nearby produces packets, so a long total silence is suspicious: restart to be safe.
            long silentMs = Environment.TickCount64 - Interlocked.Read(ref _lastPacketTick);
            if (silentMs > SilenceBeforeRestart.TotalMilliseconds)
            {
                Log.Write($"No Bluetooth broadcasts for {silentMs / 1000}s; restarting the watcher");
                _restarting = true;
                try { _watcher.Stop(); } catch { /* handled by the next call */ }
            }
            return;
        }

        if (status is BluetoothLEAdvertisementWatcherStatus.Stopped
                   or BluetoothLEAdvertisementWatcherStatus.Aborted
                   or BluetoothLEAdvertisementWatcherStatus.Created)
        {
            _restarting = false;
            Start();
        }
        // Stopping: wait for the next call.
    }

    void ReportFailure(string reason)
    {
        if (_failure != reason) Log.Write($"Watcher not running: {reason}");
        _failure = reason;
        Failed?.Invoke(reason);
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        Interlocked.Exchange(ref _lastPacketTick, Environment.TickCount64);
        if (e.RawSignalStrengthInDBm < MinRssi) return;

        foreach (var md in e.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AirPodsParser.AppleCompanyId) continue;

            var bytes = new byte[md.Data.Length];
            using (var reader = DataReader.FromBuffer(md.Data)) reader.ReadBytes(bytes);

            var state = AirPodsParser.TryParse(bytes, e.RawSignalStrengthInDBm);
            if (state is null) continue;

            Log.Write($"{Convert.ToHexString(bytes)} -> {state}");
            StateReceived?.Invoke(state);
        }
    }
}
