using ViperSynapse.Core.Engine;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>One input node of a physical device, as sysfs lists it.</summary>
public sealed record InputNode(string DevicePath, ushort VendorId, ushort ProductId, string Name, string Phys)
{
    /// <summary>The USB port path without the interface, shared by every node of one device.</summary>
    public string PortPath => Phys.LastIndexOf('/') is var slash and >= 0 ? Phys[..slash] : Phys;

    public static IReadOnlyList<InputNode> Enumerate(string sysRoot = "/sys/class/input")
    {
        var nodes = new List<InputNode>();
        if (!Directory.Exists(sysRoot))
            return nodes;
        foreach (var dir in Directory.EnumerateDirectories(sysRoot, "event*"))
        {
            string Read(string name)
            {
                try { return File.ReadAllText(Path.Combine(dir, "device", name)).Trim(); }
                catch (IOException) { return ""; }
                catch (UnauthorizedAccessException) { return ""; }
            }
            if (!ushort.TryParse(Read("id/vendor"), System.Globalization.NumberStyles.HexNumber, null, out var vendor)
                || !ushort.TryParse(Read("id/product"), System.Globalization.NumberStyles.HexNumber, null, out var product))
                continue;
            nodes.Add(new InputNode("/dev/input/" + Path.GetFileName(dir), vendor, product, Read("name"), Read("phys")));
        }
        nodes.Sort((a, b) => string.CompareOrdinal(a.DevicePath, b.DevicePath));
        return nodes;
    }
}

/// <summary>
/// Takes over one physical device's keyboard and mouse nodes: grabs them, reads each on a thread of its
/// own and feeds the remap engine, whose output goes to the virtual device. Nodes that send neither keys
/// nor relative motion (the Tartarus's gamepad and analog nodes) are left alone. Disposing lets go of
/// everything; the kernel also drops the grabs if the process dies.
/// </summary>
public sealed class DeviceSession : IDisposable
{
    readonly List<EvdevDevice> _nodes = [];
    readonly List<Thread> _threads = [];
    readonly Lock _engineGate = new();
    volatile bool _stopping;

    public RemapEngine Engine { get; }
    public IReadOnlyList<string> NodePaths => _nodes.Select(n => n.Path).ToList();

    /// <summary>Raised on a reader thread when a node goes away (unplugged) or fails.</summary>
    public event Action<string, Exception>? NodeLost;

    DeviceSession(RemapEngine engine) => Engine = engine;

    public static DeviceSession Start(IEnumerable<string> nodePaths, RemapEngine engine)
    {
        var session = new DeviceSession(engine);
        try
        {
            foreach (var path in nodePaths)
            {
                var node = EvdevDevice.Open(path);
                if (node.Capabilities(EventTypes.Key).Count == 0 && node.Capabilities(EventTypes.Rel).Count == 0)
                {
                    node.Dispose();
                    continue;
                }
                node.Grab();
                session._nodes.Add(node);
            }
            foreach (var node in session._nodes)
            {
                var thread = new Thread(() => session.Pump(node)) { IsBackground = true, Name = "input " + node.Path };
                session._threads.Add(thread);
                thread.Start();
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Changes the keymap. Keys held across the change are let go first, so none stays down.</summary>
    public void Apply(Core.Bindings.Keymap keymap)
    {
        lock (_engineGate)
        {
            Engine.Reset();
            Engine.Keymap = keymap;
        }
    }

    void Pump(EvdevDevice node)
    {
        var events = new InputEvent[64];
        try
        {
            while (!_stopping)
            {
                int n = node.Read(events, TimeSpan.FromMilliseconds(100));
                lock (_engineGate)
                {
                    for (int i = 0; i < n; i++)
                        Dispatch(events[i]);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            if (!_stopping)
                NodeLost?.Invoke(node.Path, e);
        }
    }

    void Dispatch(in InputEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Key:
                Engine.Key(e.Code, e.Value);
                break;
            case EventTypes.Rel when e.Code is RelativeAxes.Wheel or RelativeAxes.HWheel or RelativeAxes.WheelHiRes or RelativeAxes.HWheelHiRes:
                Engine.Wheel(e.Code, e.Value);
                break;
            case EventTypes.Rel:
                Engine.Relative(e.Code, e.Value);
                break;
            case EventTypes.Syn when e.Code == EventTypes.SynReport:
                Engine.Sync();
                break;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_engineGate)
            Engine.Reset();
        // Readers wake from poll within 100 ms and see the flag; only then are the nodes closed, so no
        // read can land on a descriptor number the process has already reused.
        foreach (var thread in _threads)
            thread.Join();
        foreach (var node in _nodes)
            node.Dispose();
    }
}
