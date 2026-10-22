using System.Globalization;
using ViperSynapse.Core.Devices;
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
                "scan" when args.Length >= 2 => Scan(args[1]),
                "get" when args.Length >= 4 => Get(args[1], args[2..]),
                "inputs" => Inputs(),
                "watch" when args.Length >= 2 => Watch(args[1..]),
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
            viper-cli scan NODE       every "get" command (id 0x80-0xFF) in classes 0x00-0x0F; prints the supported ones
            viper-cli get NODE CLASS ID [ARG...]   one "get" command (hex bytes; the id must have bit 7 set)
            viper-cli inputs          Razer input nodes and what each can send
            viper-cli watch NODE...   print events from input nodes (no grab)
            """);
        return 64;
    }

    static int List()
    {
        foreach (var device in HidrawNode.Enumerate().Where(n => n.VendorId == RazerVendor).GroupBy(n => n.PortPath))
        {
            var first = device.First();
            var model = DeviceCatalogue.Default.Find(first.ProductId);
            Console.WriteLine($"{first.VendorId:x4}:{first.ProductId:x4} {model?.Name ?? first.Name} ({model?.Type ?? "unknown model"})  [{device.Key}]");
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

    /// <summary>Sends every "get" (bit 7 set) with zero arguments and prints the ones the device does not
    /// refuse. Gets only: a set is never sent.</summary>
    static int Get(string path, string[] hex)
    {
        var bytes = hex.Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if ((bytes[1] & 0x80) == 0)
        {
            Console.Error.WriteLine("Refusing: the command id has bit 7 clear, which makes it a set.");
            return 64;
        }
        var arguments = new byte[RazerReport.MaxArguments];
        bytes.AsSpan(2).CopyTo(arguments);
        using var transport = new RazerTransport(HidrawDevice.Open(path)) { AnswerDelay = TimeSpan.FromMilliseconds(2) };
        Console.WriteLine(Describe(transport.Send(RazerReport.Create(0x1F, bytes[0], bytes[1], arguments, RazerReport.MaxArguments))));
        return 0;
    }

    static int Scan(string path)
    {
        using var transport = new RazerTransport(HidrawDevice.Open(path)) { AnswerDelay = TimeSpan.FromMilliseconds(2) };
        for (int cls = 0x00; cls <= 0x0F; cls++)
            for (int id = 0x80; id <= 0xFF; id++)
            {
                var answer = transport.Send(RazerReport.Create(0x1F, (byte)cls, (byte)id, new byte[RazerReport.MaxArguments], RazerReport.MaxArguments));
                if (answer.Status is not (ReportStatus.NotSupported or ReportStatus.Failure))
                    Console.WriteLine(Describe(answer));
            }
        return 0;
    }

    static int Inputs()
    {
        foreach (var dir in Directory.EnumerateDirectories("/sys/class/input", "event*").OrderBy(d => int.Parse(Path.GetFileName(d)[5..], CultureInfo.InvariantCulture)))
        {
            string Read(string name) { try { return File.ReadAllText(Path.Combine(dir, "device", name)).Trim(); } catch (IOException) { return ""; } }
            if (Read("id/vendor") != "1532")
                continue;
            string path = "/dev/input/" + Path.GetFileName(dir);
            Console.WriteLine($"{path}  {Read("id/product")}  {Read("name")}  [{Read("phys")}]");
            using var device = EvdevDevice.Open(path);
            foreach (var type in new[] { EventTypes.Key, EventTypes.Rel, EventTypes.Abs })
            {
                var codes = device.Capabilities(type);
                if (codes.Count > 0)
                    Console.WriteLine($"    {EventTypes.Name(type)} ({codes.Count}): {string.Join(' ', codes.Take(40).Select(c => EventTypes.CodeName(type, c)))}{(codes.Count > 40 ? " …" : "")}");
            }
        }
        return 0;
    }

    static int Watch(string[] paths)
    {
        var threads = paths.Select(path => new Thread(() =>
        {
            using var device = EvdevDevice.Open(path);
            var events = new InputEvent[64];
            while (true)
            {
                int n = device.Read(events);
                for (int i = 0; i < n; i++)
                    if (events[i].Type != EventTypes.Syn)
                        Console.WriteLine($"{Path.GetFileName(path)} {events[i]}");
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
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
