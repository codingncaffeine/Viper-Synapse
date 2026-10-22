using System.Runtime.InteropServices;

namespace ViperSynapse.Hardware.Linux;

/// <summary>The handful of libc calls device access needs.</summary>
internal static partial class Libc
{
    public const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    public const int EINTR = 4, EAGAIN = 11, ENODEV = 19;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint Write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static unsafe partial int Ioctl(int fd, nuint request, void* argument);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, nint argument);

    // _IOC(dir, type, nr, size) from asm-generic/ioctl.h.
    const uint IocWrite = 1, IocRead = 2;

    static nuint Ioc(uint dir, char type, uint nr, uint size) =>
        (nuint)((dir << 30) | (size << 16) | ((uint)type << 8) | nr);

    public static nuint IoR(char type, uint nr, uint size) => Ioc(IocRead, type, nr, size);
    public static nuint IoW(char type, uint nr, uint size) => Ioc(IocWrite, type, nr, size);
    public static nuint IoWR(char type, uint nr, uint size) => Ioc(IocRead | IocWrite, type, nr, size);
    public static nuint Io(char type, uint nr) => Ioc(0, type, nr, 0);

    public static string ErrorText(int errno) => Marshal.GetPInvokeErrorMessage(errno);
}
