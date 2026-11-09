using System.Runtime.InteropServices;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>
/// The keys a mouse's firmware-only buttons were told to send (F13 and up, see DeviceKey.RelayUsage), read from the
/// mouse's keyboard interface over hidraw: 8-byte boot keyboard reports (modifiers, reserved, six key usages).
/// Read here rather than from the input node because another program may hold that node grabbed (a hotkey
/// daemon such as gsr-global-hotkeys grabs every keyboard), and hidraw cannot be grabbed. Only listening.
/// </summary>
public sealed class RelayButtons : IDisposable
{
    readonly int _fd;
    readonly Thread _thread;
    readonly IReadOnlySet<byte> _watched;
    readonly HashSet<byte> _down = [];
    volatile bool _stopping;

    /// <summary>A watched usage went down (true) or up (false). Raised on the reader thread.</summary>
    public event Action<byte, bool>? Changed;

    RelayButtons(int fd, string path, IReadOnlySet<byte> watched)
    {
        _fd = fd;
        _watched = watched;
        _thread = new Thread(Listen) { IsBackground = true, Name = "relay buttons " + path };
    }

    public static RelayButtons Start(string hidrawPath, IReadOnlySet<byte> usages)
    {
        int fd = Libc.Open(hidrawPath, Libc.O_RDONLY | Libc.O_CLOEXEC);
        if (fd < 0)
            throw new IOException($"{hidrawPath}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        var buttons = new RelayButtons(fd, hidrawPath, usages);
        buttons._thread.Start();
        return buttons;
    }

    /// <summary>Reads one report and raises a change for each watched usage that went down or up. Public for the tests.</summary>
    public void Handle(ReadOnlySpan<byte> report)
    {
        if (report.Length < 3)
            return;
        var now = new HashSet<byte>();
        foreach (byte usage in report[2..])
            if (_watched.Contains(usage))
                now.Add(usage);
        foreach (byte usage in now.Where(u => !_down.Contains(u)).ToList())
        {
            _down.Add(usage);
            Changed?.Invoke(usage, true);
        }
        foreach (byte usage in _down.Where(u => !now.Contains(u)).ToList())
        {
            _down.Remove(usage);
            Changed?.Invoke(usage, false);
        }
    }

    unsafe void Listen()
    {
        var buffer = new byte[64];
        var poll = new Libc.PollFd { Fd = _fd, Events = Libc.POLLIN };
        while (!_stopping)
        {
            poll.Revents = 0;
            int ready = Libc.Poll(&poll, 1, 100);
            if (ready <= 0)
                continue;
            if ((poll.Revents & (Libc.POLLERR | Libc.POLLHUP | Libc.POLLNVAL)) != 0)
                return;
            nint n;
            fixed (byte* b = buffer)
                n = Libc.Read(_fd, b, (nuint)buffer.Length);
            if (n <= 0)
                return;
            Handle(buffer.AsSpan(0, (int)n));
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join();
        Libc.Close(_fd);
    }
}
