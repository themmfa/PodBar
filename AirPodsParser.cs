namespace PodBar;

/// <summary>One snapshot of what the AirPods are broadcasting. Battery values are percentages, null when unknown.</summary>
public sealed record AirPodsState(
    string Model,
    int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging,
    bool LeftInEar, bool RightInEar,
    short Rssi);

/// <summary>
/// Parses Apple's "proximity pairing" BLE advertisement (manufacturer 0x004C, message type 0x07).
/// Layout of the 27 bytes that follow the company ID:
///   [0]    0x07  message type
///   [1]    0x19  payload length (25)
///   [3..4] model ID, little endian
///   [5]    status: bit 0x20 = left pod is the one broadcasting, bits 0x02 / 0x08 = in-ear flags
///   [6]    pod batteries, one nibble each (0-10 = tens of percent, 15 = unknown)
///   [7]    high nibble = charging flags, low nibble = case battery
/// When the right pod is broadcasting, the left/right fields are swapped ("flipped").
/// </summary>
public static class AirPodsParser
{
    public const ushort AppleCompanyId = 0x004C;

    public static AirPodsState? TryParse(byte[] d, short rssi)
    {
        if (d.Length != 27 || d[0] != 0x07 || d[1] != 0x19) return null;

        ushort modelId = (ushort)(d[3] | (d[4] << 8));
        byte status = d[5];
        bool flip = (status & 0x20) == 0;

        int hi = d[6] >> 4, lo = d[6] & 0x0F;
        int charge = d[7] >> 4;

        return new AirPodsState(
            Model: ModelName(modelId),
            Left: ToPercent(flip ? hi : lo),
            Right: ToPercent(flip ? lo : hi),
            Case: ToPercent(d[7] & 0x0F),
            LeftCharging: (charge & (flip ? 0x2 : 0x1)) != 0,
            RightCharging: (charge & (flip ? 0x1 : 0x2)) != 0,
            CaseCharging: (charge & 0x4) != 0,
            LeftInEar: (status & (flip ? 0x08 : 0x02)) != 0,
            RightInEar: (status & (flip ? 0x02 : 0x08)) != 0,
            Rssi: rssi);
    }

    static int? ToPercent(int nibble) => nibble <= 10 ? nibble * 10 : null;

    static string ModelName(ushort id) => id switch
    {
        0x2002 => "AirPods (1st gen)",
        0x200F => "AirPods (2nd gen)",
        0x2013 => "AirPods (3rd gen)",
        0x2019 => "AirPods 4",
        0x201B => "AirPods 4 (ANC)",
        0x200E => "AirPods Pro",
        0x2014 => "AirPods Pro 2",
        0x2024 => "AirPods Pro 2 (USB-C)",
        0x200A => "AirPods Max",
        _ => $"AirPods / Beats (0x{id:X4})",
    };
}
