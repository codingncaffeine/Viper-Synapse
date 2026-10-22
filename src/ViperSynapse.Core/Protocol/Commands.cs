namespace ViperSynapse.Core.Protocol;

/// <summary>
/// The command class / id pairs shared across Razer devices. A "get" is the "set" id with bit 7 set.
/// Only reads appear in <see cref="SafeReads"/>: those never change the device and are what a probe
/// of an unknown device may send.
/// </summary>
public static class Commands
{
    public readonly record struct Command(byte Class, byte Id, byte DataSize, string Name);

    // Class 0x00: device information and modes.
    public static readonly Command GetFirmware = new(0x00, 0x81, 0x02, "firmware version");
    public static readonly Command GetSerial = new(0x00, 0x82, 0x16, "serial number");
    public static readonly Command GetDeviceMode = new(0x00, 0x84, 0x02, "device mode");
    public static readonly Command GetPollingRate = new(0x00, 0x85, 0x01, "polling rate");

    // Class 0x04: sensor.
    public static readonly Command GetDpi = new(0x04, 0x85, 0x07, "DPI");
    public static readonly Command GetDpiStages = new(0x04, 0x86, 0x26, "DPI stages");

    // Class 0x07: power.
    public static readonly Command GetBattery = new(0x07, 0x80, 0x02, "battery level");
    public static readonly Command GetCharging = new(0x07, 0x84, 0x02, "charging status");

    // Class 0x0F: extended matrix lighting.
    public static readonly Command GetBrightness = new(0x0F, 0x84, 0x03, "brightness");

    public static readonly IReadOnlyList<Command> SafeReads =
        [GetFirmware, GetSerial, GetDeviceMode, GetPollingRate, GetDpi, GetDpiStages, GetBattery, GetCharging, GetBrightness];

    /// <summary>Transaction ids seen across Razer generations: newest first.</summary>
    public static readonly IReadOnlyList<byte> KnownTransactionIds = [0x1F, 0x3F, 0x9F, 0xFF, 0x08];

    public static RazerReport Request(byte transactionId, Command command, params byte[] arguments) =>
        RazerReport.Create(transactionId, command.Class, command.Id, arguments, command.DataSize);
}
