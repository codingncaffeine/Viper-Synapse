using System.Runtime.InteropServices;
using System.Text;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Hardware.Linux;

/// <summary>
/// A virtual keyboard and mouse made through /dev/uinput: what the remap engine types and clicks with.
/// It declares every key code, the mouse buttons, pointer motion and both wheels. Writes from several
/// threads are serialised; each <see cref="Sync"/> ends one group.
/// </summary>
public sealed class UinputDevice : IInputSink, IDisposable
{
    int _fd;
    readonly Lock _gate = new();

    public string Name { get; }

    UinputDevice(int fd, string name)
    {
        _fd = fd;
        Name = name;
    }

    // linux/uinput.h
    static readonly nuint UiDevCreate = Libc.Io('U', 1);
    static readonly nuint UiDevDestroy = Libc.Io('U', 2);
    static readonly nuint UiDevSetup = Libc.IoW('U', 3, 92);
    static readonly nuint UiSetEvBit = Libc.IoW('U', 100, sizeof(int));
    static readonly nuint UiSetKeyBit = Libc.IoW('U', 101, sizeof(int));
    static readonly nuint UiSetRelBit = Libc.IoW('U', 102, sizeof(int));
    static readonly nuint UiSetPhys = Libc.IoW('U', 108, (uint)IntPtr.Size);
    static readonly nuint UiSetAbsBit = Libc.IoW('U', 103, sizeof(int));
    static readonly nuint UiAbsSetup = Libc.IoW('U', 4, 28);
    static nuint UiGetSysname(int length) => Libc.IoR('U', 44, (uint)length);

    // phys: the physical path the device claims (tests use it to stand in for a USB device); none by default.
    public static unsafe UinputDevice Create(string name, ushort vendor = 0x1532, ushort product = 0xF5F5, string? phys = null, string path = "/dev/uinput")
    {
        int fd = Libc.Open(path, Libc.O_WRONLY | Libc.O_NONBLOCK | Libc.O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            string message = $"{path}: {Libc.ErrorText(errno)}";
            throw errno == 13 ? new UnauthorizedAccessException(message) : new IOException(message);
        }
        var device = new UinputDevice(fd, name);
        try
        {
            device.Set(UiSetEvBit, EventTypes.Key);
            device.Set(UiSetEvBit, EventTypes.Rel);
            device.Set(UiSetEvBit, EventTypes.Syn);
            foreach (var code in KeyCodes.All.Where(KeyCodes.IsKeyboardOrMouse))
                device.Set(UiSetKeyBit, code);
            foreach (var axis in new[] { RelativeAxes.X, RelativeAxes.Y, RelativeAxes.Wheel, RelativeAxes.HWheel, RelativeAxes.WheelHiRes, RelativeAxes.HWheelHiRes })
                device.Set(UiSetRelBit, axis);

            // struct uinput_setup { struct input_id { u16 bustype, vendor, product, version }; char name[80]; u32 ff_effects_max; }
            var setup = new byte[92];
            BitConverter.TryWriteBytes(setup.AsSpan(0), (ushort)0x03);   // BUS_USB
            BitConverter.TryWriteBytes(setup.AsSpan(2), vendor);
            BitConverter.TryWriteBytes(setup.AsSpan(4), product);
            BitConverter.TryWriteBytes(setup.AsSpan(6), (ushort)1);
            Encoding.UTF8.GetBytes(name.Length > 79 ? name[..79] : name).CopyTo(setup.AsSpan(8));
            fixed (byte* p = setup)
                device.Check(Libc.Ioctl(fd, UiDevSetup, p), "set up the virtual device");
            if (phys != null)
            {
                var bytes = Encoding.UTF8.GetBytes(phys + "\0");
                fixed (byte* p = bytes)
                    device.Check(Libc.Ioctl(fd, UiSetPhys, p), "set the physical path");
            }
            device.Check(Libc.Ioctl(fd, UiDevCreate, 0), "create the virtual device");
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>A stand-in for a keypad's gamepad node, for tests: the right stick (RX, RY, -32768 to 32767) and its
    /// click. Never used outside tests.</summary>
    public static unsafe UinputDevice CreateTestGamepad(string name, ushort vendor, ushort product, string phys, string path = "/dev/uinput")
    {
        int fd = Libc.Open(path, Libc.O_WRONLY | Libc.O_NONBLOCK | Libc.O_CLOEXEC);
        if (fd < 0)
            throw new IOException($"{path}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        var device = new UinputDevice(fd, name);
        try
        {
            device.Set(UiSetEvBit, EventTypes.Key);
            device.Set(UiSetEvBit, EventTypes.Abs);
            device.Set(UiSetEvBit, EventTypes.Syn);
            foreach (var button in new ushort[] { 0x130, 0x131, 0x13E })   // BTN_SOUTH, BTN_EAST, BTN_THUMBR
                device.Set(UiSetKeyBit, button);
            foreach (ushort axis in new ushort[] { 3, 4 })                   // ABS_RX, ABS_RY
            {
                device.Set(UiSetAbsBit, axis);
                // struct uinput_abs_setup { u16 code; (2 padding); struct input_absinfo { s32 value, min, max, fuzz, flat, resolution } }
                var setup = new byte[28];
                BitConverter.TryWriteBytes(setup.AsSpan(0), axis);
                BitConverter.TryWriteBytes(setup.AsSpan(8), -32768);
                BitConverter.TryWriteBytes(setup.AsSpan(12), 32767);
                fixed (byte* p = setup)
                    device.Check(Libc.Ioctl(fd, UiAbsSetup, p), "set up an axis");
            }
            var info = new byte[92];
            BitConverter.TryWriteBytes(info.AsSpan(0), (ushort)0x03);
            BitConverter.TryWriteBytes(info.AsSpan(2), vendor);
            BitConverter.TryWriteBytes(info.AsSpan(4), product);
            Encoding.UTF8.GetBytes(name).CopyTo(info.AsSpan(8));
            fixed (byte* p = info)
                device.Check(Libc.Ioctl(fd, UiDevSetup, p), "set up the virtual device");
            var physBytes = Encoding.UTF8.GetBytes(phys + "\0");
            fixed (byte* p = physBytes)
                device.Check(Libc.Ioctl(fd, UiSetPhys, p), "set the physical path");
            device.Check(Libc.Ioctl(fd, UiDevCreate, 0), "create the virtual device");
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public void Absolute(ushort axis, int value) => Write(EventTypes.Abs, axis, value);

    /// <summary>The /dev/input/eventN node the kernel made for this device, or null if it cannot be found.</summary>
    public unsafe string? EventNode
    {
        get
        {
            var buffer = new byte[64];
            int n;
            fixed (byte* p = buffer)
                n = Libc.Ioctl(_fd, UiGetSysname(buffer.Length), p);
            if (n < 0)
                return null;
            string sysname = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
            var dir = Path.Combine("/sys/devices/virtual/input", sysname);
            var events = Directory.Exists(dir) ? Directory.GetDirectories(dir, "event*") : [];
            return events.Length == 0 ? null : "/dev/input/" + Path.GetFileName(events[0]);
        }
    }

    public void Key(ushort code, int value) => Write(EventTypes.Key, code, value);

    public void Relative(ushort axis, int value) => Write(EventTypes.Rel, axis, value);

    public void Sync() => Write(EventTypes.Syn, EventTypes.SynReport, 0);

    unsafe void Write(ushort type, ushort code, int value)
    {
        var e = new InputEvent { Type = type, Code = code, Value = value };
        lock (_gate)
        {
            if (_fd < 0)
                return;
            nint n = Libc.Write(_fd, (byte*)&e, InputEvent.Size);
            if (n != InputEvent.Size)
                throw new IOException($"uinput: could not write an event: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        }
    }

    void Set(nuint request, int bit) => Check(Libc.Ioctl(_fd, request, bit), "declare a capability");

    void Check(int result, string what)
    {
        if (result < 0)
            throw new IOException($"uinput: could not {what}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_fd < 0)
                return;
            Libc.Ioctl(_fd, UiDevDestroy, 0);
            Libc.Close(_fd);
            _fd = -1;
        }
    }
}
