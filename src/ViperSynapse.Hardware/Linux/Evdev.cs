using System.Runtime.InteropServices;
using System.Text;

namespace ViperSynapse.Hardware.Linux;

/// <summary>One input event as the kernel delivers it (struct input_event on 64-bit Linux).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct InputEvent
{
    public long Seconds;
    public long Microseconds;
    public ushort Type;
    public ushort Code;
    public int Value;

    public const int Size = 24;

    public readonly TimeSpan Time => TimeSpan.FromSeconds(Seconds) + TimeSpan.FromMicroseconds(Microseconds);

    public override readonly string ToString() => $"{Time.TotalSeconds:F6} {EventTypes.Name(Type)} {EventTypes.CodeName(Type, Code)} {Value}";
}

/// <summary>Event types and the codes the remap engine cares about (linux/input-event-codes.h).</summary>
public static class EventTypes
{
    public const ushort Syn = 0x00, Key = 0x01, Rel = 0x02, Abs = 0x03, Msc = 0x04;
    public const ushort SynReport = 0;
    public const ushort MscScan = 4;

    public static string Name(ushort type) => type switch
    {
        Syn => "SYN", Key => "KEY", Rel => "REL", Abs => "ABS", Msc => "MSC", _ => $"0x{type:x2}",
    };

    public static string CodeName(ushort type, ushort code) => type switch
    {
        Key => ViperSynapse.Core.Input.KeyCodes.Name(code),
        Rel => code switch { 0 => "X", 1 => "Y", 6 => "HWHEEL", 8 => "WHEEL", 11 => "WHEEL_HI_RES", 12 => "HWHEEL_HI_RES", _ => code.ToString() },
        Abs => code switch { 0 => "X", 1 => "Y", 2 => "Z", 3 => "RX", 4 => "RY", 5 => "RZ", 16 => "HAT0X", 17 => "HAT0Y", _ => code.ToString() },
        _ => code.ToString(),
    };
}

/// <summary>An open /dev/input/eventN node: read events, grab it so nobody else sees them, read its capabilities.</summary>
public sealed class EvdevDevice : IDisposable
{
    int _fd;

    public string Path { get; }

    EvdevDevice(string path, int fd)
    {
        Path = path;
        _fd = fd;
    }

    public static EvdevDevice Open(string path)
    {
        int fd = Libc.Open(path, Libc.O_RDONLY | Libc.O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            string message = $"{path}: {Libc.ErrorText(errno)}";
            throw errno == 13 ? new UnauthorizedAccessException(message) : new IOException(message);
        }
        return new EvdevDevice(path, fd);
    }

    // EVIOCGNAME(len) = _IOR('E', 0x06, len); EVIOCGBIT(ev, len) = _IOR('E', 0x20 + ev, len); EVIOCGRAB = _IOW('E', 0x90, int).
    public unsafe string Name
    {
        get
        {
            var buffer = new byte[256];
            fixed (byte* p = buffer)
            {
                int n = Libc.Ioctl(_fd, Libc.IoR('E', 0x06, (uint)buffer.Length), p);
                return n <= 0 ? "" : Encoding.UTF8.GetString(buffer, 0, n).TrimEnd('\0');
            }
        }
    }

    /// <summary>The codes this node can send for one event type (or the event types themselves, for type 0).</summary>
    public unsafe IReadOnlyList<ushort> Capabilities(ushort type)
    {
        var bits = new byte[0x300 / 8 + 1];
        int n;
        fixed (byte* p = bits)
            n = Libc.Ioctl(_fd, Libc.IoR('E', 0x20u + type, (uint)bits.Length), p);
        var codes = new List<ushort>();
        for (int i = 0; i < Math.Max(n, 0) * 8; i++)
            if ((bits[i / 8] & (1 << (i % 8))) != 0)
                codes.Add((ushort)i);
        return codes;
    }

    /// <summary>Takes the device for ourselves: its events stop reaching the desktop until <see cref="Release"/>
    /// or until this handle closes (the kernel drops a grab with its file, so a crash cannot leave a dead device).</summary>
    public void Grab()
    {
        Check(Libc.Ioctl(_fd, Libc.IoW('E', 0x90, sizeof(int)), 1), "grab");
        IsGrabbed = true;
    }

    public void Release()
    {
        Check(Libc.Ioctl(_fd, Libc.IoW('E', 0x90, sizeof(int)), 0), "release");
        IsGrabbed = false;
    }

    public bool IsGrabbed { get; private set; }

    /// <summary>Whether any key or button of the device is down right now, by the kernel's own record
    /// (EVIOCGKEY = _IOR('E', 0x18, len)).</summary>
    public unsafe bool AnyKeyDown()
    {
        var bits = new byte[0x300 / 8 + 1];
        int n;
        fixed (byte* p = bits)
            n = Libc.Ioctl(_fd, Libc.IoR('E', 0x18, (uint)bits.Length), p);
        if (n < 0)
            throw new IOException($"{Path}: could not read the key state: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        return bits.Any(b => b != 0);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for events; returns how many landed in <paramref name="events"/>
    /// (0 when none came). Throws <see cref="IOException"/> when the device goes away.</summary>
    public unsafe int Read(Span<InputEvent> events, TimeSpan timeout)
    {
        var poll = new Libc.PollFd { Fd = _fd, Events = Libc.POLLIN };
        int ready = Libc.Poll(&poll, 1, (int)timeout.TotalMilliseconds);
        if (ready < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == Libc.EINTR)
                return 0;
            throw new IOException($"{Path}: {Libc.ErrorText(errno)}");
        }
        if (ready == 0)
            return 0;
        if ((poll.Revents & (Libc.POLLERR | Libc.POLLHUP | Libc.POLLNVAL)) != 0)
            throw new IOException($"{Path}: the device went away");
        return Read(events);
    }

    /// <summary>Blocks until the device sends events; returns how many landed in <paramref name="events"/>.
    /// Throws <see cref="IOException"/> when the device goes away.</summary>
    public unsafe int Read(Span<InputEvent> events)
    {
        fixed (InputEvent* p = events)
        {
            while (true)
            {
                nint n = Libc.Read(_fd, (byte*)p, (nuint)(events.Length * InputEvent.Size));
                if (n >= 0)
                    return (int)(n / InputEvent.Size);
                int errno = Marshal.GetLastPInvokeError();
                if (errno != Libc.EINTR)
                    throw new IOException($"{Path}: {Libc.ErrorText(errno)}");
            }
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
