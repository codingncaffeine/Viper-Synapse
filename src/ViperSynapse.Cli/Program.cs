using System.Globalization;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Cli;

static class Program
{
    const ushort RazerVendor = 0x1532;

    static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "list" => List(),
                "probe" when args.Length >= 2 => Probe(args[1]),
                _ => Usage(),
            };
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"{e.Message}\nInstall packaging/udev/70-viper-synapse.rules so Razer devices belong to the logged-in user.");
            return 2;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("""
            viper-cli list            Razer hidraw nodes, grouped by device
            viper-cli probe NODE      read-only commands against a control node (e.g. /dev/hidraw17)
            """);
        return 64;
    }

    static int List()
    {
        foreach (var device in HidrawNode.Enumerate().Where(n => n.VendorId == RazerVendor).GroupBy(n => n.PortPath))
        {
            var first = device.First();
            Console.WriteLine($"{first.VendorId:x4}:{first.ProductId:x4} {first.Name}  [{device.Key}]");
            foreach (var node in device)
            {
                bool control = HasControlReport(node);
                Console.WriteLine($"    interface {node.Interface}: {node.DevicePath}{(control ? "  (control)" : "")}");
            }
        }
        return 0;
    }

    /// <summary>The control interface declares one 90-byte feature report (Report Count 0x5A then a Feature item).</summary>
    static bool HasControlReport(HidrawNode node)
    {
        var descriptor = Path.Combine("/sys/class/hidraw", Path.GetFileName(node.DevicePath), "device", "report_descriptor");
        try
        {
            // sysfs reports 4096 bytes and delivers fewer, which File.ReadAllBytes treats as an error.
            using var stream = File.OpenRead(descriptor);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            for (int i = 0; i + 3 < bytes.Length; i++)
                if (bytes[i] == 0x95 && bytes[i + 1] == 0x5A && bytes[i + 2] == 0xB1)
                    return true;
        }
        catch (IOException) { }
        return false;
    }

    /// <summary>Finds the transaction id the device answers to, then reads everything it is willing to report.</summary>
    static int Probe(string path)
    {
        using var transport = new RazerTransport(HidrawDevice.Open(path)) { AnswerDelay = TimeSpan.FromMilliseconds(2) };
        byte? transaction = null;
        foreach (var id in Commands.KnownTransactionIds)
        {
            var answer = transport.Send(Commands.Request(id, Commands.GetSerial));
            Console.WriteLine($"transaction 0x{id:x2}: {Describe(answer)}");
            if (answer.Status == ReportStatus.Successful && answer.Answers(Commands.Request(id, Commands.GetSerial)))
            {
                transaction = id;
                break;
            }
        }
        if (transaction is not byte tid)
        {
            Console.WriteLine("No transaction id answered.");
            return 1;
        }
        foreach (var command in Commands.SafeReads)
        {
            var answer = transport.Send(Commands.Request(tid, command));
            Console.WriteLine($"{command.Name,-16} {Describe(answer)}");
        }
        return 0;
    }

    static string Describe(RazerReport r)
    {
        var args = r.Arguments[..Math.Min((int)r.DataSize, RazerReport.MaxArguments)];
        string hex = string.Join(' ', args.ToArray().Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        string ascii = new(args.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        return $"{r.Status,-12} class {r.CommandClass:x2} id {r.CommandId:x2} size {r.DataSize,2}  {hex}  |{ascii}|";
    }
}
