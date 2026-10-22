using System.Globalization;

namespace ViperSynapse.Hardware.Linux;

/// <summary>One /dev/hidrawN node as sysfs describes it: whose it is and which USB interface it sits on.</summary>
public sealed record HidrawNode(string DevicePath, ushort VendorId, ushort ProductId, int Interface, string Name, string Phys)
{
    /// <summary>The physical device the node belongs to (the USB port path without the interface), so
    /// the interfaces of one device group together.</summary>
    public string PortPath
    {
        get
        {
            int slash = Phys.LastIndexOf('/');
            return slash < 0 ? Phys : Phys[..slash];
        }
    }

    /// <summary>Every hidraw node in the system, read from /sys/class/hidraw.</summary>
    public static IReadOnlyList<HidrawNode> Enumerate(string sysRoot = "/sys/class/hidraw", string devRoot = "/dev")
    {
        var nodes = new List<HidrawNode>();
        if (!Directory.Exists(sysRoot))
            return nodes;
        foreach (var dir in Directory.EnumerateDirectories(sysRoot))
        {
            var node = FromUevent(Path.Combine(devRoot, Path.GetFileName(dir)), ReadOrEmpty(Path.Combine(dir, "device", "uevent")));
            if (node != null)
                nodes.Add(node);
        }
        nodes.Sort((a, b) => string.CompareOrdinal(a.Phys, b.Phys));
        return nodes;
    }

    /// <summary>Parses the HID device's uevent: HID_ID=0003:00001532:000002EB, HID_NAME=…, HID_PHYS=usb-…/input2.</summary>
    public static HidrawNode? FromUevent(string devicePath, string uevent)
    {
        string? id = null, name = null, phys = null;
        foreach (var line in uevent.Split('\n'))
        {
            if (line.StartsWith("HID_ID=", StringComparison.Ordinal)) id = line[7..];
            else if (line.StartsWith("HID_NAME=", StringComparison.Ordinal)) name = line[9..];
            else if (line.StartsWith("HID_PHYS=", StringComparison.Ordinal)) phys = line[9..];
        }
        if (id == null || phys == null)
            return null;
        var parts = id.Split(':');
        if (parts.Length != 3
            || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor)
            || !uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var product))
            return null;
        int iface = -1;
        int at = phys.LastIndexOf("/input", StringComparison.Ordinal);
        if (at >= 0)
            _ = int.TryParse(phys.AsSpan(at + 6), NumberStyles.None, CultureInfo.InvariantCulture, out iface);
        return new HidrawNode(devicePath, (ushort)vendor, (ushort)product, iface, name ?? "", phys);
    }

    static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
