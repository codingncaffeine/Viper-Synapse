using Tmds.DBus.Protocol;

namespace ViperSynapse.App.Services;

/// <summary>
/// One window per session. The first copy holds org.vipersynapse.Window on the session bus; a second copy
/// asks it to show itself and exits. That is how a window hidden in the tray comes back on a desktop with no
/// tray to click (GNOME without the AppIndicator extension): start Viper Synapse again from the menu.
/// </summary>
public sealed class WindowBus : IPathMethodHandler, IDisposable
{
    const string Name = "org.vipersynapse.Window", ObjectPath = "/org/vipersynapse/Window", Interface = "org.vipersynapse.Window1";

    DBusConnection? _connection;

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    public event Action? ShowRequested;

    /// <summary>True when this is the only copy; false when another one was asked to show itself.</summary>
    public async Task<bool> ClaimAsync()
    {
        if (DBusAddress.Session is not { } address)
            return true;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            connection.AddMethodHandler(this);
            if (await connection.TryRequestNameAsync(Name, RequestNameOptions.None).ConfigureAwait(false))
            {
                _connection = connection;
                return true;
            }
            await connection.CallMethodAsync(ShowMessage(connection)).ConfigureAwait(false);
            connection.Dispose();
            return false;
        }
        catch (Exception e) when (e is DBusConnectionException or DBusErrorReplyException or IOException)
        {
            // Without a bus there is no way to find the other copy; run anyway.
            connection.Dispose();
            return true;
        }
    }

    static MessageBuffer ShowMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Name, path: ObjectPath, @interface: Interface, member: "Show");
        return writer.CreateMessage();
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.InterfaceAsString == Interface && context.Request.MemberAsString == "Show")
        {
            ShowRequested?.Invoke();
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
        else
        {
            context.ReplyUnknownMethodError();
        }
        return default;
    }

    public void Dispose() => _connection?.Dispose();
}
