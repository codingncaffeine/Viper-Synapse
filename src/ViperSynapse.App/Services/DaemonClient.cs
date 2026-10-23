using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tmds.DBus.Protocol;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.App.Services;

/// <summary>A connected device as the service describes it.</summary>
/// <summary>The device's own sensitivity, polling rate and battery.</summary>
public sealed record DeviceState(IReadOnlyList<ViperSynapse.Core.Lighting.DpiStage>? Stages, int ActiveStage, int PollingRate, int? BatteryPercent, bool? Charging, int LiftOffLevel = -1,
    int SleepSeconds = 0, int LowBatteryPercent = 0, bool? ScrollAcceleration = null, bool? SmartReel = null);

public sealed record DeviceInfo(
    string Key, string ProductId, string Name, string Type, string Port, bool Known, bool Verified,
    string? Serial, string? Firmware, string? ControlNode, IReadOnlyList<string> InputNodes, bool Remapping, string? Problem);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DeviceInfo>))]
[JsonSerializable(typeof(DeviceState))]
internal sealed partial class ClientJson : JsonSerializerContext;

/// <summary>
/// The window's line to viper-synapsed on the session bus. Every call is asynchronous and completes on a
/// pool thread; callers marshal results to the UI thread themselves. When the service is not running, the
/// client starts the copy installed next to the window (a development build, or a session without the
/// systemd unit) and waits for it to take its name.
/// </summary>
public sealed class DaemonClient : IAsyncDisposable
{
    const string BusName = "org.vipersynapse.Daemon", ObjectPath = "/org/vipersynapse/Daemon", Interface = "org.vipersynapse.Daemon1";

    DBusConnection? _connection;

    public event Action? DevicesChanged;
    public event Action<string>? SettingsChanged;

    /// <summary>A watched thumbstick moved: device, x, y (-1 to 1, y negative upwards). Raised on a pool thread.</summary>
    public event Action<string, double, double>? StickMoved;

    public async Task ConnectAsync(CancellationToken cancel = default)
    {
        var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
        await connection.ConnectAsync().ConfigureAwait(false);
        _connection = connection;
        await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = Interface, Path = ObjectPath },
            static (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                return m.SignatureAsString switch
                {
                    "s" => (m.MemberAsString ?? "", r.ReadString(), 0.0, 0.0),
                    "sdd" => (m.MemberAsString ?? "", r.ReadString(), r.ReadDouble(), r.ReadDouble()),
                    _ => (m.MemberAsString ?? "", (string?)null, 0.0, 0.0),
                };
            },
            (Notification<(string Member, string? Arg, double X, double Y)> n) =>
            {
                if (!n.HasValue) return;
                var signal = n.Value;
                if (signal.Member == "DevicesChanged") DevicesChanged?.Invoke();
                else if (signal.Member == "SettingsChanged" && signal.Arg != null) SettingsChanged?.Invoke(signal.Arg);
                else if (signal.Member == "StickMoved" && signal.Arg != null) StickMoved?.Invoke(signal.Arg, signal.X, signal.Y);
            },
            emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);

        if (await TryPingAsync().ConfigureAwait(false))
        {
            // A service from another build (an update installed while it ran) is replaced by this build's own.
            if (await CallStringAsync("BuildId").ConfigureAwait(false) == Service.DaemonBus.BuildId)
                return;
            await QuitServiceAsync().ConfigureAwait(false);
            for (int i = 0; i < 50 && await TryPingAsync().ConfigureAwait(false); i++)
                await Task.Delay(100, cancel).ConfigureAwait(false);
        }
        StartService();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            await Task.Delay(100, cancel).ConfigureAwait(false);
            if (await TryPingAsync().ConfigureAwait(false))
                return;
        }
        throw new TimeoutException("The Viper Synapse service did not start. Is the udev rule installed?");
    }

    async Task<bool> TryPingAsync()
    {
        try
        {
            await CallStringAsync("Ping").ConfigureAwait(false);
            return true;
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    static void StartService()
    {
        var next = Path.Combine(AppContext.BaseDirectory, "viper-synapsed");
        var info = File.Exists(next) ? new ProcessStartInfo(next) : new ProcessStartInfo("viper-synapsed");
        info.UseShellExecute = false;
        try
        {
            using var _ = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync() =>
        JsonSerializer.Deserialize(await CallStringAsync("GetDevices").ConfigureAwait(false), ClientJson.Default.ListDeviceInfo) ?? [];

    public async Task<DeviceState?> GetDeviceStateAsync(string device) =>
        JsonSerializer.Deserialize(await CallStringAsync("GetDeviceState", device).ConfigureAwait(false), ClientJson.Default.DeviceState);

    public async Task<DeviceSettings> GetSettingsAsync(string device) =>
        SettingsJson.ReadSettings(await CallStringAsync("GetSettings", device).ConfigureAwait(false));

    public Task SetSettingsAsync(string device, DeviceSettings settings) =>
        CallAsync("SetSettings", "ss", device, SettingsJson.Write(settings));

    public async Task<MacroLibrary> GetMacrosAsync() =>
        SettingsJson.ReadMacros(await CallStringAsync("GetMacros").ConfigureAwait(false));

    public Task SetMacrosAsync(MacroLibrary macros) => CallAsync("SetMacros", "s", SettingsJson.Write(macros));

    public async Task<ShortcutList> GetShortcutsAsync() =>
        SettingsJson.ReadShortcuts(await CallStringAsync("GetShortcuts").ConfigureAwait(false));

    public Task SetShortcutsAsync(ShortcutList shortcuts) => CallAsync("SetShortcuts", "s", SettingsJson.Write(shortcuts));

    /// <summary>Follow the device's thumbstick for the next 15 seconds (ask again to keep following).</summary>
    public Task WatchStickAsync(string device) => CallAsync("WatchStick", "s", device);

    public Task StartRecordingAsync() => CallAsync("StartRecording", null);

    /// <summary>An on-screen notification through the desktop's notification service (org.freedesktop.Notifications),
    /// which every Linux desktop provides. Failing quietly: a desktop without one simply shows nothing.</summary>
    public async Task NotifyAsync(string summary, string body)
    {
        try
        {
            await Connection.CallMethodAsync(NotifyMessage(Connection, summary, body)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
        }
    }

    static MessageBuffer NotifyMessage(DBusConnection connection, string summary, string body)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.Notifications", path: "/org/freedesktop/Notifications",
            @interface: "org.freedesktop.Notifications", member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("Viper Synapse");
        writer.WriteUInt32(0);
        writer.WriteString("viper-synapse");
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(Array.Empty<string>());
        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(3000);
        return writer.CreateMessage();
    }

    /// <summary>Stops the service: Quit from the tray ends bindings and macros with the app.</summary>
    public Task QuitServiceAsync() => CallAsync("Quit", null);

    public async Task<IReadOnlyList<MacroStep>> StopRecordingAsync(RecordDelayMode mode, int defaultDelayMs, int randomMinMs = 0, int randomMaxMs = 0)
    {
        var connection = Connection;
        string json = await connection.CallMethodAsync(StopRecordingMessage(connection, mode, defaultDelayMs, randomMinMs, randomMaxMs),
            static (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);
        return SettingsJson.ReadSteps(json);
    }

    static MessageBuffer StopRecordingMessage(DBusConnection connection, RecordDelayMode mode, int defaultDelayMs, int randomMinMs, int randomMaxMs)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: ObjectPath, @interface: Interface, member: "StopRecording", signature: "siii");
        writer.WriteString(mode.ToString());
        writer.WriteInt32(defaultDelayMs);
        writer.WriteInt32(randomMinMs);
        writer.WriteInt32(randomMaxMs);
        return writer.CreateMessage();
    }

    DBusConnection Connection => _connection ?? throw new InvalidOperationException("Not connected.");

    // A MessageWriter cannot live across an await, so messages are built in plain methods.
    static MessageBuffer Build(DBusConnection connection, string member, string? signature, string[] arguments)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: ObjectPath, @interface: Interface, member: member, signature: signature);
        foreach (var argument in arguments)
            writer.WriteString(argument);
        return writer.CreateMessage();
    }

    async Task<string> CallStringAsync(string member, string? argument = null)
    {
        var connection = Connection;
        var message = argument == null ? Build(connection, member, null, []) : Build(connection, member, "s", [argument]);
        return await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);
    }

    async Task CallAsync(string member, string? signature, params string[] arguments)
    {
        var connection = Connection;
        await connection.CallMethodAsync(Build(connection, member, signature, arguments)).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
