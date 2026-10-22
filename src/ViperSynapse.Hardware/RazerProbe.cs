using System.Text;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>What a device says about itself when asked with read-only commands.</summary>
public sealed record DeviceIdentity(byte TransactionId, string Serial, string Firmware);

/// <summary>Finds a device's control interface and the transaction id it answers to. Reads only: nothing
/// sent here changes the device.</summary>
public static class RazerProbe
{
    /// <summary>True when the node's report descriptor declares the 90-byte control feature report
    /// (Report Count 0x5A followed by a Feature item).</summary>
    public static bool HasControlReport(HidrawNode node, string sysRoot = "/sys/class/hidraw")
    {
        var descriptor = Path.Combine(sysRoot, Path.GetFileName(node.DevicePath), "device", "report_descriptor");
        try
        {
            // sysfs reports 4096 bytes and delivers fewer, which File.ReadAllBytes treats as an error.
            using var stream = File.OpenRead(descriptor);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            for (int i = 0; i + 2 < bytes.Length; i++)
                if (bytes[i] == 0x95 && bytes[i + 1] == 0x5A && bytes[i + 2] == 0xB1)
                    return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Asks for the serial number with each known transaction id until one is answered, then reads
    /// the firmware version. Null when the device answers none.</summary>
    public static DeviceIdentity? Identify(RazerTransport transport)
    {
        foreach (var id in Commands.KnownTransactionIds)
        {
            var request = Commands.Request(id, Commands.GetSerial);
            var answer = transport.Send(request);
            if (answer.Status != ReportStatus.Successful || !answer.Answers(request))
                continue;
            string serial = Encoding.ASCII.GetString(answer.Arguments[..Math.Min((int)answer.DataSize, 22)]).TrimEnd('\0', ' ');
            var firmware = transport.Send(Commands.Request(id, Commands.GetFirmware));
            string version = firmware.Status == ReportStatus.Successful
                ? $"v{firmware.Arguments[0]}.{firmware.Arguments[1]:D2}"
                : "";
            return new DeviceIdentity(id, serial, version);
        }
        return null;
    }
}
