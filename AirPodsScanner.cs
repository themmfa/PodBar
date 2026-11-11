using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PodBar;

/// <summary>Listens for BLE advertisements and raises <see cref="StateReceived"/> for nearby AirPods.</summary>
public sealed class AirPodsScanner
{
    /// <summary>Normal cut-off: ignore anything weaker, so a neighbour's AirPods are not picked up.</summary>
    public const short DefaultMinRssi = -70;

    /// <summary>Looser cut-off for when the AirPods are connected to this PC and so known to be the user's.</summary>
    public const short ConnectedMinRssi = -85;

    /// <summary>Broadcasts weaker than this (in dBm) are ignored.</summary>
    public volatile short MinRssi = DefaultMinRssi;

    readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };

    /// <summary>Raised on a background thread.</summary>
    public event Action<AirPodsState>? StateReceived;

    /// <summary>Raised on a background thread when scanning stops because of an error (e.g. Bluetooth is off).</summary>
    public event Action<string>? Failed;

    public AirPodsScanner()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            Log.Write($"Watcher stopped: {e.Error}");
            if (e.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
                Failed?.Invoke(e.Error.ToString());
        };
    }

    public void Start()
    {
        try
        {
            _watcher.Start();
            Log.Write("Watcher started");
        }
        catch (Exception ex)
        {
            Log.Write($"Watcher failed to start: {ex.Message}");
            Failed?.Invoke(ex.Message);
        }
    }

    public void Stop()
    {
        try { _watcher.Stop(); } catch { /* already stopped */ }
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
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
