using System.Globalization;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Service;

/// <summary>What the window is told about one connected device.</summary>
public sealed record DeviceInfo(
    string Key,
    string ProductId,
    string Name,
    string Type,
    string Port,
    bool Known,
    bool Verified,
    string? Serial,
    string? Firmware,
    string? ControlNode,
    IReadOnlyList<string> InputNodes,
    bool Remapping,
    string? Problem);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DeviceInfo>))]
internal sealed partial class DeviceInfoJson : JsonSerializerContext;

/// <summary>
/// Keeps track of the Razer devices plugged in and runs a remap session for each one whose active keymap
/// binds anything. A device with nothing bound is left alone (not grabbed), so it behaves exactly as it does
/// without the service. All state changes happen under one lock; device I/O happens outside it where it
/// can take time.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    public const ushort RazerVendor = 0x1532;

    /// <summary>Model ids verified on real hardware; the rest of the catalogue is OpenRazer's word.</summary>
    static readonly HashSet<ushort> Verified = [0x02EB, 0x008D];

    readonly SettingsStore _store;
    readonly IInputSink _output;
    readonly Lock _gate = new();
    readonly Dictionary<string, Connected> _devices = [];
    MacroLibrary _macros;

    public event Action? DevicesChanged;
    public event Action<string>? SettingsChanged;

    public DeviceManager(SettingsStore store, IInputSink output)
    {
        _store = store;
        _output = output;
        _macros = store.LoadMacros();
    }

    sealed class Connected(string port, ushort productId, string name)
    {
        public string Port { get; } = port;
        public ushort ProductId { get; } = productId;
        public string Name { get; } = name;
        public string Key => ProductId.ToString("x4", CultureInfo.InvariantCulture);
        public DeviceModel? Model => DeviceCatalogue.Default.Find(ProductId);
        public string? ControlNode { get; set; }
        public DeviceIdentity? Identity { get; set; }
        public List<string> InputNodes { get; set; } = [];
        public RemapEngine? Engine { get; set; }
        public DeviceSession? Session { get; set; }
        public DeviceControl? Control { get; set; }
        public string? Problem { get; set; }
    }

    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            lock (_gate)
                return _devices.Values.OrderBy(d => d.Port, StringComparer.Ordinal).Select(Describe).ToList();
        }
    }

    public string DevicesJson() => System.Text.Json.JsonSerializer.Serialize(Devices.ToList(), DeviceInfoJson.Default.ListDeviceInfo);

    static DeviceInfo Describe(Connected d) => new(
        d.Key,
        d.Key,
        d.Model?.Name ?? d.Name,
        d.Model?.Type ?? "unknown",
        d.Port,
        d.Model != null,
        Verified.Contains(d.ProductId),
        d.Identity?.Serial,
        d.Identity?.Firmware,
        d.ControlNode,
        d.InputNodes,
        d.Session != null,
        d.Problem);

    /// <summary>Looks at what is plugged in now: starts new devices, drops the ones that went away.</summary>
    public void Refresh()
    {
        var hidraw = HidrawNode.Enumerate().Where(n => n.VendorId == RazerVendor && n.PortPath.Length > 0).ToList();
        // Virtual devices (ours included) have no physical port; only real hardware is a device.
        var inputs = InputNode.Enumerate().Where(n => n.VendorId == RazerVendor && n.PortPath.Length > 0).ToList();
        var ports = hidraw.Select(n => (n.PortPath, n.ProductId, n.Name))
            .Concat(inputs.Select(n => (n.PortPath, n.ProductId, n.Name)))
            .GroupBy(p => p.PortPath)
            .ToDictionary(g => g.Key, g => g.First());

        bool changed = false;
        List<Connected> added = [];
        lock (_gate)
        {
            foreach (var gone in _devices.Keys.Where(port => !ports.ContainsKey(port)).ToList())
            {
                _devices[gone].Session?.Dispose();
                _devices[gone].Control?.Dispose();
                _devices.Remove(gone);
                changed = true;
            }
            foreach (var (port, (_, productId, name)) in ports)
            {
                if (!_devices.TryGetValue(port, out var device))
                {
                    device = new Connected(port, productId, name);
                    _devices[port] = device;
                    added.Add(device);
                    changed = true;
                }
                var nodes = inputs.Where(n => n.PortPath == port).Select(n => n.DevicePath).ToList();
                if (!nodes.SequenceEqual(device.InputNodes))
                {
                    device.InputNodes = nodes;
                    // The device's nodes changed under a running session (a re-plug): start it again.
                    device.Session?.Dispose();
                    device.Session = null;
                    changed = true;
                }
            }
        }

        // Probing talks to the hardware; keep it out of the lock.
        foreach (var device in added)
        {
            var control = hidraw.FirstOrDefault(n => n.PortPath == device.Port && RazerProbe.HasControlReport(n));
            device.ControlNode = control?.DevicePath;
            if (control != null)
            {
                try
                {
                    var transport = new RazerTransport(HidrawDevice.Open(control.DevicePath));
                    device.Identity = RazerProbe.Identify(transport);
                    if (device.Identity == null)
                    {
                        transport.Dispose();
                        device.Problem = "The device did not answer on its control interface.";
                    }
                    else
                    {
                        // Kept open while the device is plugged in: lighting and sensitivity go through it.
                        device.Control = new DeviceControl(transport, device.Identity.TransactionId, device.Model);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    device.Problem = e is UnauthorizedAccessException
                        ? "No permission to talk to the device: install the Viper Synapse udev rule."
                        : e.Message;
                }
            }
        }

        List<string> keys;
        lock (_gate)
            keys = _devices.Values.Select(d => d.Key).Distinct().ToList();
        foreach (var key in keys)
            Apply(key);
        if (changed)
            DevicesChanged?.Invoke();
    }

    public DeviceSettings GetSettings(string key) => _store.LoadDevice(key);

    /// <summary>The device's own sensitivity, polling rate and battery, read now. Null when no device of the model is
    /// plugged in or it has no control interface.</summary>
    public DeviceState? ReadState(string key)
    {
        DeviceControl? control;
        lock (_gate)
            control = _devices.Values.FirstOrDefault(d => d.Key == key && d.Control != null)?.Control;
        return control?.ReadState();
    }

    public void SetSettings(string key, DeviceSettings settings)
    {
        _store.SaveDevice(key, settings);
        Apply(key);
        SettingsChanged?.Invoke(key);
    }

    public MacroLibrary Macros
    {
        get { lock (_gate) return _macros; }
    }

    public void SetMacros(MacroLibrary macros)
    {
        _store.SaveMacros(macros);
        lock (_gate)
            _macros = macros;
    }

    /// <summary>Brings every connected device of a model in line with its stored settings.</summary>
    public void Apply(string key)
    {
        var settings = _store.LoadDevice(key);
        var keymap = settings.ActiveKeymapOrFirst;
        bool bindsAnything = keymap.Standard.Values.Concat(keymap.Hypershift.Values).Any(b => b is not DefaultBinding);
        bool changed = false;
        lock (_gate)
        {
            foreach (var device in _devices.Values.Where(d => d.Key == key))
            {
                if (!bindsAnything)
                {
                    if (device.Session != null)
                    {
                        device.Session.Dispose();
                        device.Session = null;
                        changed = true;
                    }
                    continue;
                }
                if (device.Session != null)
                {
                    device.Session.Apply(keymap);
                    continue;
                }
                device.Engine = new RemapEngine(_output, new Host(this, key)) { Keymap = keymap };
                try
                {
                    device.Session = DeviceSession.Start(device.InputNodes, device.Engine);
                    device.Session.NodeLost += (_, _) => ThreadPool.QueueUserWorkItem(_ => Refresh());
                    device.Problem = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    device.Problem = "Could not take over the device's keys: " + e.Message;
                }
                changed = true;
            }
        }
        // Lighting, sensitivity and power talk to the hardware: outside the lock.
        List<DeviceControl> controls;
        lock (_gate)
            controls = _devices.Values.Where(d => d.Key == key && d.Control != null).Select(d => d.Control!).ToList();
        foreach (var control in controls)
        {
            control.Apply(settings.Active);
            if (control.LastProblem != null)
                Console.Error.WriteLine($"{key}: {control.LastProblem}");
        }
        if (changed)
            DevicesChanged?.Invoke();
    }

    // Per device model: the profile the user chose, while a linked program's profile stands in for it.
    readonly Dictionary<string, Guid> _chosenProfile = [];

    /// <summary>A program came to the front: switch each device to the profile linked to it, or back to the one the
    /// user had chosen when the front program is linked to none.</summary>
    public void Focused(FocusedProgram program)
    {
        List<string> keys;
        lock (_gate)
            keys = _devices.Values.Select(d => d.Key).Distinct().ToList();
        foreach (var key in keys)
        {
            var settings = _store.LoadDevice(key);
            var linked = settings.Profiles.FirstOrDefault(p => p.LinkedPrograms.Any(program.Matches));
            Guid? target;
            lock (_gate)
            {
                if (linked != null)
                {
                    _chosenProfile.TryAdd(key, settings.ActiveProfile);
                    target = linked.Id;
                }
                else
                {
                    target = _chosenProfile.Remove(key, out var chosen) ? chosen : null;
                }
            }
            if (target is Guid id && id != settings.ActiveProfile && settings.Profiles.Any(p => p.Id == id))
                SetSettings(key, settings with { ActiveProfile = id, ActiveKeymap = 0 });
        }
    }

    /// <summary>What a device's engine asks of the service.</summary>
    sealed class Host(DeviceManager manager, string key) : IEngineHost
    {
        public void Launch(string command)
        {
            try { CommandLine.Start(command); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"Could not start \"{command}\": {e.Message}");
            }
        }

        // Switching changes the keymap the engine runs, which must not happen on the engine's own input thread
        // while it is mid-event; the pool takes it.
        public void SwitchProfile(CycleDirection direction, Guid? profileId) =>
            ThreadPool.QueueUserWorkItem(_ => manager.SetSettings(key, manager.GetSettings(key).SwitchProfile(direction, profileId)));

        public void SwitchKeymap(CycleDirection direction, int keymap) =>
            ThreadPool.QueueUserWorkItem(_ => manager.SetSettings(key, manager.GetSettings(key).SwitchKeymap(direction, keymap)));

        public Macro? FindMacro(Guid id) => manager.Macros.Find(id);

        public void Report(Exception error) => Console.Error.WriteLine($"{key}: {error.Message}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                device.Session?.Dispose();
                device.Control?.Dispose();
            }
            _devices.Clear();
        }
    }
}
