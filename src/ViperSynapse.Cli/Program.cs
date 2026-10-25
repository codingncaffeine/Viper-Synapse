using System.Globalization;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
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
                "set" when args.Length >= 4 => Set(args[1], args[2..]),
                "inputs" => Inputs(),
                "watch" when args.Length >= 2 => Watch(args[1..]),
                "raw" when args.Length >= 2 => Raw(args[1..]),
                "bench" when args.Length >= 3 => Bench(args[1], args[2..]),
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
            viper-cli set NODE CLASS ID [ARG...]   one "set": lighting (0F), DPI (04 05/06) and polling rate (00 05/40) only
            viper-cli inputs          Razer input nodes and what each can send
            viper-cli watch NODE...   print events from input nodes (no grab)
            viper-cli raw HIDRAW...    print input reports from hidraw nodes (changes only), e.g. a vendor interface
            viper-cli bench NODE frame ROWS COLS | zones LED...   frames per second a software effect gets: a moving hue to
                                      the live slot for 3 s (per-key rows, or a static colour per LED). Leaves the device on it.
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
                bool control = RazerProbe.HasControlReport(node);
                Console.WriteLine($"    interface {node.Interface}: {node.DevicePath}{(control ? "  (control)" : "")}");
            }
        }
        return 0;
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

    /// <summary>Only commands known to be safe on any Razer device: lighting, DPI and polling rate. Device mode
    /// (00 04) resets some keypads and is refused, as is everything else.</summary>
    static int Set(string path, string[] hex)
    {
        var bytes = hex.Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        bool allowed = bytes[0] == 0x0F && bytes[1] is 0x02 or 0x03 or 0x04
            || bytes[0] == 0x04 && bytes[1] is 0x05 or 0x06
            || bytes[0] == 0x00 && bytes[1] is 0x05 or 0x40
            // Sensor settings: calibration mode, tracking mode, lift-off table. Never 0x09 (starts surface sampling).
            || bytes[0] == 0x0B && bytes[1] is 0x03 or 0x05 or 0x0B
            // Selecting a keymap (as the keypad's own keymap button does), for reading each preset.
            || bytes[0] == 0x02 && bytes[1] == 0x37
            // The scroll wheel: acceleration and Smart-Reel (OpenRazer's 02 16 and 02 17).
            || bytes[0] == 0x02 && bytes[1] is 0x16 or 0x17
            // Power: low battery threshold and sleep timer.
            || bytes[0] == 0x07 && bytes[1] is 0x01 or 0x03;
        if (!allowed)
        {
            Console.Error.WriteLine("Refusing: only lighting (0F 02/03/04), DPI (04 05/06), polling rate (00 05/40), sensor settings (0B 03/05/0B), keymaps (02 37) the scroll wheel (02 16/17) and power (07 01/03) may be set from here.");
            return 64;
        }
        var arguments = bytes.AsSpan(2).ToArray();
        using var transport = new RazerTransport(HidrawDevice.Open(path)) { AnswerDelay = TimeSpan.FromMilliseconds(2) };
        Console.WriteLine(Describe(transport.Send(RazerReport.Create(0x1F, bytes[0], bytes[1], arguments))));
        return 0;
    }

    /// <summary>How many frames a second a software effect can send: a hue moving across the LEDs, live slot only, for
    /// three seconds. Per-key devices get their rows; zone devices a static colour per LED.</summary>
    static int Bench(string path, string[] spec)
    {
        using var transport = new RazerTransport(HidrawDevice.Open(path));
        if (RazerProbe.Identify(transport) is not { } identity)
        {
            Console.Error.WriteLine("The device did not answer.");
            return 1;
        }
        byte tid = identity.TransactionId;
        bool frame = spec[0] == "frame" && spec.Length == 3;
        int rows = frame ? int.Parse(spec[1], CultureInfo.InvariantCulture) : 0, columns = frame ? int.Parse(spec[2], CultureInfo.InvariantCulture) : 0;
        byte[] leds = frame ? [] : spec.Skip(1).Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        int frames = 0, refused = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            double t = clock.Elapsed.TotalSeconds / 3;
            if (frame)
            {
                for (int r = 0; r < rows; r++)
                {
                    var colors = Enumerable.Range(0, columns).Select(c => Rgb.FromHue(t + (double)c / columns)).ToList();
                    if (transport.Send(DeviceCommands.CustomFrameRow(tid, r, 0, columns - 1, colors)).Status != ReportStatus.Successful)
                        refused++;
                }
                if (frames == 0 && transport.Send(DeviceCommands.CustomFrameEffect(tid)).Status != ReportStatus.Successful)
                    refused++;
            }
            else
            {
                for (int i = 0; i < leds.Length; i++)
                {
                    var effect = new ZoneEffect(EffectKind.Static, Rgb.FromHue(t + (double)i / leds.Length), default);
                    if (transport.Send(DeviceCommands.Effect(tid, leds[i], effect, DeviceCommands.NoStore)).Status != ReportStatus.Successful)
                        refused++;
                }
            }
            frames++;
        }
        Console.WriteLine($"{frames / clock.Elapsed.TotalSeconds:F1} frames/s ({frames} frames, {refused} refused)");
        return refused == 0 ? 0 : 1;
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

    /// <summary>Input reports straight from hidraw nodes, printed only when a node's report changes.</summary>
    static unsafe int Raw(string[] paths)
    {
        var threads = paths.Select(path => new Thread(() =>
        {
            int fd = Libc.Open(path, Libc.O_RDONLY | Libc.O_CLOEXEC);
            if (fd < 0) { Console.Error.WriteLine($"{path}: cannot open"); return; }
            var buffer = new byte[256];
            string last = "";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                nint n;
                fixed (byte* p = buffer)
                    n = Libc.Read(fd, p, (nuint)buffer.Length);
                if (n <= 0) break;
                string hex = string.Join(' ', buffer.Take((int)n).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                if (hex == last) continue;
                last = hex;
                Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3} {Path.GetFileName(path)} {hex}");
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
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
