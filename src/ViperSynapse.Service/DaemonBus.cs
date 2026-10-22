using Tmds.DBus.Protocol;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;

namespace ViperSynapse.Service;

/// <summary>
/// The service on the session bus: org.vipersynapse.Daemon at /org/vipersynapse/Daemon. Settings, macros and
/// the device list cross as JSON strings, so the window and the service only share the Core types and the
/// wire format can grow without new D-Bus signatures.
/// </summary>
public sealed class DaemonBus : IPathMethodHandler, IDisposable
{
    public const string BusName = "org.vipersynapse.Daemon";
    public const string ObjectPath = "/org/vipersynapse/Daemon";
    public const string Interface = "org.vipersynapse.Daemon1";
    const string InvalidError = "org.vipersynapse.Error.Invalid";

    static readonly ReadOnlyMemory<byte> Introspection = System.Text.Encoding.UTF8.GetBytes($"""
        <interface name="{Interface}">
          <method name="Ping"><arg direction="out" type="s" name="version"/></method>
          <method name="GetDevices"><arg direction="out" type="s" name="json"/></method>
          <method name="GetSettings"><arg direction="in" type="s" name="device"/><arg direction="out" type="s" name="json"/></method>
          <method name="SetSettings"><arg direction="in" type="s" name="device"/><arg direction="in" type="s" name="json"/></method>
          <method name="GetDeviceState"><arg direction="in" type="s" name="device"/><arg direction="out" type="s" name="json"/></method>
          <method name="GetMacros"><arg direction="out" type="s" name="json"/></method>
          <method name="SetMacros"><arg direction="in" type="s" name="json"/></method>
          <method name="ActiveWindow"><arg direction="in" type="s" name="windowClass"/><arg direction="in" type="v" name="pid"/></method>
          <method name="BuildId"><arg direction="out" type="s" name="id"/></method>
          <method name="WatchStick"><arg direction="in" type="s" name="device"/></method>
          <signal name="StickMoved"><arg type="s" name="device"/><arg type="d" name="x"/><arg type="d" name="y"/></signal>
          <method name="Quit"/>
          <method name="StartRecording"/>
          <method name="StopRecording"><arg direction="in" type="s" name="delayMode"/><arg direction="in" type="i" name="defaultDelayMs"/><arg direction="in" type="i" name="randomMinMs"/><arg direction="in" type="i" name="randomMaxMs"/><arg direction="out" type="s" name="steps"/></method>
          <signal name="DevicesChanged"/>
          <signal name="SettingsChanged"><arg type="s" name="device"/></signal>
        </interface>
        """);

    readonly DeviceManager _devices;
    readonly Func<IEnumerable<string>> _recordFrom;
    readonly Action _quit;
    readonly Lock _recordGate = new();
    DBusConnection? _connection;
    MacroRecorder? _recorder;

    /// <summary>KWin's focus script reports here.</summary>
    public event Action<string, uint>? ActiveWindow;

    /// <summary>Changes with every build: the window replaces a service from a different build.</summary>
    public static string BuildId { get; } = typeof(DaemonBus).Assembly.ManifestModule.ModuleVersionId.ToString();

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    /// <param name="devices">The devices the service runs.</param>
    /// <param name="recordFrom">The input nodes a recording listens to.</param>
    /// <param name="quit">Stops the service: the window's Quit, so remaps end with the app as in Synapse.</param>
    public DaemonBus(DeviceManager devices, Func<IEnumerable<string>> recordFrom, Action quit)
    {
        _quit = quit;
        _devices = devices;
        _recordFrom = recordFrom;
        devices.DevicesChanged += () => Signal("DevicesChanged", null);
        devices.SettingsChanged += key => Signal("SettingsChanged", key);
        devices.StickMoved += SignalStick;
    }

    /// <summary>Connects and takes the bus name. Fails when another service already holds it.</summary>
    public async Task ConnectAsync(string? address = null)
    {
        var connection = new DBusConnection(address ?? DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
        await connection.ConnectAsync().ConfigureAwait(false);
        connection.AddMethodHandler(this);
        if (!await connection.TryRequestNameAsync(BusName, RequestNameOptions.None).ConfigureAwait(false))
        {
            connection.Dispose();
            throw new InvalidOperationException($"{BusName} is already taken: is viper-synapsed running already?");
        }
        _connection = connection;
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        // Device and file work stays off the connection's own thread.
        return new ValueTask(Task.Run(() =>
        {
            try { Handle(context); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                context.ReplyError(InvalidError, e.Message);
            }
        }));
    }

    void Handle(MethodContext context)
    {
        var request = context.Request;
        string member = request.MemberAsString ?? "";
        if (request.InterfaceAsString == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
        {
            context.ReplyIntrospectXml([Introspection]);
            return;
        }
        if (request.InterfaceAsString != Interface)
        {
            context.ReplyUnknownMethodError();
            return;
        }
        var reader = request.GetBodyReader();
        switch (member)
        {
            case "Ping":
                ReplyString(context, typeof(DaemonBus).Assembly.GetName().Version?.ToString(3) ?? "0");
                break;
            case "BuildId":
                ReplyString(context, BuildId);
                break;
            case "GetDevices":
                ReplyString(context, _devices.DevicesJson());
                break;
            case "GetSettings":
                ReplyString(context, SettingsJson.Write(_devices.GetSettings(DeviceKey(reader.ReadString()))));
                break;
            case "SetSettings":
            {
                string key = DeviceKey(reader.ReadString());
                _devices.SetSettings(key, SettingsJson.ReadSettings(reader.ReadString()));
                ReplyEmpty(context);
                break;
            }
            case "GetDeviceState":
            {
                var state = _devices.ReadState(DeviceKey(reader.ReadString()));
                ReplyString(context, state == null ? "null" : System.Text.Json.JsonSerializer.Serialize(state, DeviceStateJson.Default.DeviceState));
                break;
            }
            case "GetMacros":
                ReplyString(context, SettingsJson.Write(_devices.Macros));
                break;
            case "SetMacros":
                _devices.SetMacros(SettingsJson.ReadMacros(reader.ReadString()));
                ReplyEmpty(context);
                break;
            case "ActiveWindow":
            {
                // KWin's script sends the process id as whatever number type JavaScript hands it.
                string windowClass = reader.ReadString();
                uint pid = 0;
                var signature = request.SignatureAsString ?? "";
                if (signature.Length > 1)
                    pid = signature[1] switch
                    {
                        'i' => (uint)Math.Max(0, reader.ReadInt32()),
                        'u' => reader.ReadUInt32(),
                        'x' => (uint)Math.Max(0, reader.ReadInt64()),
                        't' => (uint)reader.ReadUInt64(),
                        'd' => (uint)Math.Max(0, reader.ReadDouble()),
                        _ => 0,
                    };
                ActiveWindow?.Invoke(windowClass, pid);
                ReplyEmpty(context);
                break;
            }
            case "WatchStick":
                _devices.WatchStick(DeviceKey(reader.ReadString()));
                ReplyEmpty(context);
                break;
            case "Quit":
                ReplyEmpty(context);
                _quit();
                break;
            case "StartRecording":
                lock (_recordGate)
                {
                    _recorder?.Dispose();
                    _recorder = MacroRecorder.Start(_recordFrom());
                }
                ReplyEmpty(context);
                break;
            case "StopRecording":
            {
                var mode = Enum.Parse<RecordDelayMode>(reader.ReadString(), ignoreCase: true);
                int defaultDelay = reader.ReadInt32();
                int randomMin = reader.ReadInt32(), randomMax = reader.ReadInt32();
                IReadOnlyList<RecordedEvent> events;
                lock (_recordGate)
                {
                    events = _recorder?.Stop() ?? [];
                    _recorder = null;
                }
                ReplyString(context, SettingsJson.Write(MacroRecording.ToSteps(TrimStopClick(events), mode, defaultDelay, randomMin, randomMax)));
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// <summary>The click on the window's Stop button is the last thing recorded; it is not part of the macro.</summary>
    static IEnumerable<RecordedEvent> TrimStopClick(IReadOnlyList<RecordedEvent> events)
    {
        int end = events.Count;
        while (end > 0 && !events[end - 1].IsWheel && KeyCodes.IsMouseButton(events[end - 1].Code) && events[end - 1].Value == 1)
            end--;
        return events.Take(end);
    }

    /// <summary>Device keys are product ids in hex; nothing else may reach a file name.</summary>
    static string DeviceKey(string key) =>
        key.Length == 4 && key.All(Uri.IsHexDigit) ? key.ToLowerInvariant() : throw new ArgumentException($"Not a device key: {key}");

    static void ReplyString(MethodContext context, string value)
    {
        using var writer = context.CreateReplyWriter("s");
        writer.WriteString(value);
        context.Reply(writer.CreateMessage());
    }

    static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    void SignalStick(string device, double x, double y)
    {
        var connection = _connection;
        if (connection == null)
            return;
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(path: ObjectPath, @interface: Interface, member: "StickMoved", signature: "sdd");
        writer.WriteString(device);
        writer.WriteDouble(x);
        writer.WriteDouble(y);
        connection.TrySendMessage(writer.CreateMessage());
    }

    void Signal(string member, string? argument)
    {
        var connection = _connection;
        if (connection == null)
            return;
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(path: ObjectPath, @interface: Interface, member: member, signature: argument == null ? null : "s");
        if (argument != null)
            writer.WriteString(argument);
        connection.TrySendMessage(writer.CreateMessage());
    }

    public void Dispose()
    {
        lock (_recordGate)
            _recorder?.Dispose();
        _connection?.Dispose();
    }
}
