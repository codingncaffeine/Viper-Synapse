using Tmds.DBus.Protocol;

namespace ViperSynapse.App.Services;

/// <summary>
/// One window per session. The first copy holds org.vipersynapse.Window on the session bus. A second copy of the
/// same build asks it to show itself and exits: that is how a window hidden in the tray comes back on a desktop
/// with no tray to click. A second copy of a different build (an update installed while the old one sat in the
/// tray) asks the old one to quit and takes over, so the new version is what runs.
/// </summary>
public sealed class WindowBus : IPathMethodHandler, IDisposable
{
    const string Name = "org.vipersynapse.Window", ObjectPath = "/org/vipersynapse/Window", Interface = "org.vipersynapse.Window1";

    /// <summary>Changes with every build of the window.</summary>
    public static string BuildId { get; } = typeof(WindowBus).Assembly.ManifestModule.ModuleVersionId.ToString();

    DBusConnection? _connection;

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    public event Action? ShowRequested;

    /// <summary>Asked by a newer build: quit, service included, so it can take over.</summary>
    public event Action? ReplaceRequested;

    /// <summary>True when this copy should run; false when another copy of the same build was asked to show itself.</summary>
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
            string running = await connection.CallMethodAsync(Call(connection, "BuildId"),
                static (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);
            if (running == BuildId)
            {
                await connection.CallMethodAsync(Call(connection, "Show")).ConfigureAwait(false);
                connection.Dispose();
                return false;
            }
            await connection.CallMethodAsync(Call(connection, "Replace")).ConfigureAwait(false);
            connection.Dispose();
            // A connection asks for a name once; each try after the old copy lets go is a fresh one.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200).ConfigureAwait(false);
                var retry = new DBusConnection(address);
                await retry.ConnectAsync().ConfigureAwait(false);
                retry.AddMethodHandler(this);
                if (await retry.TryRequestNameAsync(Name, RequestNameOptions.None).ConfigureAwait(false))
                {
                    _connection = retry;
                    return true;
                }
                retry.Dispose();
            }
            // The old copy did not let go; run anyway rather than leave the user with nothing.
            return true;
        }
        catch (Exception e) when (e is DBusConnectionException or DBusErrorReplyException or IOException)
        {
            // Without a bus there is no way to find the other copy; run anyway.
            connection.Dispose();
            return true;
        }
    }

    static MessageBuffer Call(DBusConnection connection, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Name, path: ObjectPath, @interface: Interface, member: member);
        return writer.CreateMessage();
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.InterfaceAsString != Interface)
        {
            context.ReplyUnknownMethodError();
            return default;
        }
        switch (context.Request.MemberAsString)
        {
            case "BuildId":
            {
                using var writer = context.CreateReplyWriter("s");
                writer.WriteString(BuildId);
                context.Reply(writer.CreateMessage());
                return default;
            }
            case "Show":
                ShowRequested?.Invoke();
                break;
            case "Replace":
                ReplaceRequested?.Invoke();
                break;
            default:
                context.ReplyUnknownMethodError();
                return default;
        }
        using (var writer = context.CreateReplyWriter(null))
            context.Reply(writer.CreateMessage());
        return default;
    }

    public void Dispose() => _connection?.Dispose();
}
