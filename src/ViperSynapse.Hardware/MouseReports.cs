using System.Runtime.InteropServices;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>
/// What a Razer mouse reports on its interface 1 about itself (read from the Basilisk V4 Pro, 2026-11-11): report
/// 05 02 carries the sensitivity it switched to, X and Y big-endian (05 02 04 B0 04 B0 is 1200 DPI), sent when its own
/// DPI button changes stage. Only listening: the reports reach the system as they always do.
/// </summary>
public sealed class MouseReports : IDisposable
{
    readonly int _fd;
    readonly Thread _thread;
    volatile bool _stopping;

    /// <summary>The mouse switched to this sensitivity (X, Y). Raised on the reader thread.</summary>
    public event Action<int, int>? DpiChanged;

    MouseReports(int fd, string path)
    {
        _fd = fd;
        _thread = new Thread(Listen) { IsBackground = true, Name = "mouse reports " + path };
    }

    public static MouseReports Start(string hidrawPath)
    {
        int fd = Libc.Open(hidrawPath, Libc.O_RDONLY | Libc.O_CLOEXEC);
        if (fd < 0)
            throw new IOException($"{hidrawPath}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        var reports = new MouseReports(fd, hidrawPath);
        reports._thread.Start();
        return reports;
    }

    /// <summary>Reads one report and says what it means. Public for the tests.</summary>
    public void Handle(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 6 && report[0] == 0x05 && report[1] == 0x02)
        {
            int x = (report[2] << 8) | report[3], y = (report[4] << 8) | report[5];
            if (x > 0 && y > 0)
                DpiChanged?.Invoke(x, y);
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
