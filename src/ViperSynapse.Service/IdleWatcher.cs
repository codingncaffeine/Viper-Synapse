using Tmds.DBus.Protocol;

namespace ViperSynapse.Service;

/// <summary>
/// Whether the screen is off (locked or blanked) and how long the session has been idle, for Synapse's "switch off
/// lighting" rules. Both come from the desktop over the session bus: org.freedesktop.ScreenSaver (KDE, GNOME and
/// most others; its ActiveChanged signal arrives at once) and the idle time from KDE's GetSessionIdleTime or
/// GNOME's Mutter idle monitor, read every 15 seconds. A desktop that offers neither simply never dims.
/// </summary>
public sealed class IdleWatcher : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    DBusConnection? _connection;
    bool _screenOff;

    /// <summary>Raised on a pool thread: screen off or not, and the idle time.</summary>
    public event Action<bool, TimeSpan>? Changed;

    public async Task StartAsync(string? address)
    {
        if (address == null)
            return;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            _connection = connection;
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.ScreenSaver", Member = "ActiveChanged" },
                static (Message m, object? _) => m.GetBodyReader().ReadBool(),
                (Notification<bool> n) =>
                {
                    if (!n.HasValue) return;
                    _screenOff = n.Value;
                    _ = ReportAsync();
                },
                emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);
            _screenOff = await TryCall(connection, "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "GetActive",
                static (Message m, object? _) => m.GetBodyReader().ReadBool()).ConfigureAwait(false);
            _ = Task.Run(PollAsync);
        }
        catch (Exception e) when (e is DBusConnectionException or IOException)
        {
            Console.Error.WriteLine($"Switch-off lighting: the desktop's screensaver cannot be reached ({e.Message}).");
        }
    }

    async Task PollAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await ReportAsync().ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task ReportAsync()
    {
        if (_connection is not { } connection)
            return;
        // KDE: seconds. GNOME: milliseconds from Mutter. Neither: zero, never idle.
        uint kde = await TryCall(connection, "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver",
            "GetSessionIdleTime", static (Message m, object? _) => m.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
        var idle = TimeSpan.FromSeconds(kde);
        if (kde == 0)
        {
            ulong gnome = await TryCall(connection, "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core",
                "org.gnome.Mutter.IdleMonitor", "GetIdletime", static (Message m, object? _) => m.GetBodyReader().ReadUInt64()).ConfigureAwait(false);
            idle = TimeSpan.FromMilliseconds(gnome);
        }
        Changed?.Invoke(_screenOff, idle);
    }

    static async Task<T> TryCall<T>(DBusConnection connection, string destination, string path, string iface, string member, MessageValueReader<T> reader)
    {
        try
        {
            return await connection.CallMethodAsync(Call(connection, destination, path, iface, member), reader, null).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            return default!;
        }
    }

    static MessageBuffer Call(DBusConnection connection, string destination, string path, string iface, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: path, @interface: iface, member: member);
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _connection?.Dispose();
    }
}
