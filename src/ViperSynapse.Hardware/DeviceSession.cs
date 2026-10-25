using ViperSynapse.Core.Bindings;
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

    readonly StickDirections _stick;
    readonly HashSet<string> _stickNodes = [];
    double _stickX, _stickY;

    /// <summary>The stick moved (4-way and 8-way mode): x and y from -1 to 1. Raised on the reader thread.</summary>
    public event Action<double, double>? StickMoved;

    /// <summary>A key or button of the device went down (its code, before any remapping). Raised on a reader thread, for
    /// the lighting effects that answer key presses.</summary>
    public event Action<ushort>? KeyPressed;

    /// <summary>Raised on a reader thread when a node goes away (unplugged) or fails.</summary>
    public event Action<string, Exception>? NodeLost;

    DeviceSession(RemapEngine engine, ThumbstickMode stick)
    {
        Engine = engine;
        _stick = new StickDirections(stick);
    }

    /// <param name="nodePaths">The device's input nodes.</param>
    /// <param name="engine">Where its keys go.</param>
    /// <param name="stick">Analog leaves the keypad's gamepad node to the system; 4-way and 8-way take it over and
    /// turn the stick into direction presses.</param>
    public static DeviceSession Start(IEnumerable<string> nodePaths, RemapEngine engine, ThumbstickMode stick = ThumbstickMode.Analog)
    {
        var session = new DeviceSession(engine, stick);
        try
        {
            foreach (var path in nodePaths)
            {
                var node = EvdevDevice.Open(path);
                var keys = node.Capabilities(EventTypes.Key);
                bool gamepad = keys.Any(Core.Input.KeyCodes.IsJoystickButton);
                if (gamepad && stick != ThumbstickMode.Analog && node.Capabilities(EventTypes.Abs).Count > 0)
                {
                    GrabIfIdle(node);
                    session._nodes.Add(node);
                    session._stickNodes.Add(node.Path);
                    continue;
                }
                // Otherwise gamepad and joystick nodes (the Tartarus V2 Pro's analog mode) carry axes the engine does
                // not pass on: grabbing one would freeze its sticks. They are left to the system.
                if (keys.Count == 0 && node.Capabilities(EventTypes.Rel).Count == 0 || gamepad)
                {
                    node.Dispose();
                    continue;
                }
                GrabIfIdle(node);
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

    /// <summary>Every node is taken (a node held down when the session started is taken once it is let go).</summary>
    public bool AllGrabbed => _nodes.All(n => n.IsGrabbed);

    /// <summary>Changes the keymap. Keys held across the change are let go first, so none stays down.</summary>
    public void Apply(Keymap keymap)
    {
        lock (_engineGate)
        {
            foreach (var (source, _) in _stick.Release())
                Engine.Key(source, 0);
            Engine.Reset();
            Engine.Keymap = keymap;
        }
    }

    /// <summary>Runs something against the engine between input events.</summary>
    public void WithEngine(Action<RemapEngine> action)
    {
        lock (_engineGate)
            action(Engine);
    }

    /// <summary>Takes the node now unless one of its keys or buttons is down. Grabbing mid-press splits the press
    /// from its release: the desktop saw the press from the real device, the release would come from the virtual
    /// one, and the desktop would hold the button forever (a slider dragged by the click that turned this on stuck
    /// to the pointer). A held node is taken by its own thread once everything is let go.</summary>
    static void GrabIfIdle(EvdevDevice node)
    {
        if (!node.AnyKeyDown())
            node.Grab();
    }

    /// <summary>Waits for every key and button of the node to be let go, then grabs it. A press that slips in between
    /// the check and the grab is caught by checking again after: the grab is let go and the wait goes on. Whatever
    /// queued before the grab already reached the desktop, so it is thrown away rather than played twice.</summary>
    void GrabWhenIdle(EvdevDevice node, Span<InputEvent> events)
    {
        while (!_stopping && !node.IsGrabbed)
        {
            if (node.AnyKeyDown())
            {
                Thread.Sleep(10);
                continue;
            }
            node.Grab();
            if (node.AnyKeyDown())
            {
                node.Release();
                continue;
            }
            while (node.Read(events, TimeSpan.Zero) > 0)
            {
            }
        }
    }

    void Pump(EvdevDevice node)
    {
        var events = new InputEvent[64];
        try
        {
            GrabWhenIdle(node, events);
            while (!_stopping)
            {
                int n = node.Read(events, TimeSpan.FromMilliseconds(100));
                bool stick = _stickNodes.Contains(node.Path);
                lock (_engineGate)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (stick)
                            DispatchStick(events[i]);
                        else
                            Dispatch(events[i]);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            if (!_stopping)
                NodeLost?.Invoke(node.Path, e);
        }
    }

    /// <summary>The keypad's gamepad node in 4-way or 8-way mode: the stick (reported as the right stick, -32768 to
    /// 32767) becomes direction presses and its click a control of its own; everything else on the node is dropped.</summary>
    void DispatchStick(in InputEvent e)
    {
        const ushort AbsX = 0, AbsY = 1, AbsRx = 3, AbsRy = 4, BtnThumbL = 0x13D, BtnThumbR = 0x13E;
        switch (e.Type)
        {
            case EventTypes.Abs when e.Code is AbsRx or AbsX:
                _stickX = Math.Clamp(e.Value / 32767.0, -1, 1);
                break;
            case EventTypes.Abs when e.Code is AbsRy or AbsY:
                _stickY = Math.Clamp(e.Value / 32767.0, -1, 1);
                break;
            case EventTypes.Key when e.Code is BtnThumbL or BtnThumbR && e.Value != 2:
                Engine.Key(SourceKeys.StickClick, e.Value);
                Engine.Sync();
                break;
            case EventTypes.Syn when e.Code == EventTypes.SynReport:
                StickMoved?.Invoke(_stickX, _stickY);
                foreach (var (source, pressed) in _stick.Move(_stickX, _stickY))
                    Engine.Key(source, pressed ? 1 : 0);
                Engine.Sync();
                break;
        }
    }

    void Dispatch(in InputEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Key:
                if (e.Value == 1)
                    KeyPressed?.Invoke(e.Code);
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
