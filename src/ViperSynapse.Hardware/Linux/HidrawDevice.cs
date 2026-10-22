using System.Runtime.InteropServices;

namespace ViperSynapse.Hardware.Linux;

/// <summary>An open hidraw node: feature reports in and out. Not thread-safe; one owner talks to it.</summary>
public sealed class HidrawDevice : IDisposable
{
    int _fd;

    public string Path { get; }

    HidrawDevice(string path, int fd)
    {
        Path = path;
        _fd = fd;
    }

    /// <summary>Opens the node read-write. Throws <see cref="UnauthorizedAccessException"/> when the
    /// udev rule that hands Razer devices to the logged-in user is missing.</summary>
    public static HidrawDevice Open(string path)
    {
        int fd = Libc.Open(path, Libc.O_RDWR | Libc.O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            string message = $"{path}: {Libc.ErrorText(errno)}";
            throw errno == 13 ? new UnauthorizedAccessException(message) : new IOException(message);
        }
        return new HidrawDevice(path, fd);
    }

    // HIDIOCSFEATURE(len) = _IOWR('H', 0x06, len), HIDIOCGFEATURE(len) = _IOWR('H', 0x07, len).
    static nuint SetFeature(int length) => Libc.IoWR('H', 0x06, (uint)length);
    static nuint GetFeature(int length) => Libc.IoWR('H', 0x07, (uint)length);

    /// <summary>Sends a feature report. <paramref name="report"/> starts with the report id.</summary>
    public unsafe void SendFeature(ReadOnlySpan<byte> report)
    {
        var buffer = report.ToArray();
        fixed (byte* p = buffer)
            Check(Libc.Ioctl(_fd, SetFeature(buffer.Length), p), "send feature report");
    }

    /// <summary>Reads a feature report into <paramref name="report"/>, whose first byte names the report id.</summary>
    public unsafe int ReceiveFeature(Span<byte> report)
    {
        fixed (byte* p = report)
        {
            int n = Libc.Ioctl(_fd, GetFeature(report.Length), p);
            Check(n, "read feature report");
            return n;
        }
    }

    void Check(int result, string what)
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);
        if (result < 0)
            throw new IOException($"{Path}: could not {what}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
    }

    public void Dispose()
    {
        if (_fd >= 0)
        {
            Libc.Close(_fd);
            _fd = -1;
        }
    }
}
