namespace ViperSynapse.Core.Protocol;

/// <summary>What the device says about the last command, in byte 0 of its reply.</summary>
public enum ReportStatus : byte
{
    New = 0x00,
    Busy = 0x01,
    Successful = 0x02,
    Failure = 0x03,
    Timeout = 0x04,
    NotSupported = 0x05,
}

/// <summary>
/// The 90-byte control report every Razer device of the last decade speaks, sent and read back as
/// a HID feature report. Layout: status, transaction id, remaining packets (big-endian), protocol
/// type, data size, command class, command id, 80 argument bytes, checksum, reserved.
/// A command id with bit 7 set is a "get"; the same id without it is the matching "set".
/// </summary>
public sealed class RazerReport
{
    public const int Size = 90;
    public const int MaxArguments = 80;

    const int StatusAt = 0, TransactionAt = 1, RemainingAt = 2, ProtocolAt = 4, DataSizeAt = 5,
        ClassAt = 6, IdAt = 7, ArgumentsAt = 8, ChecksumAt = 88;

    readonly byte[] _bytes;

    RazerReport(byte[] bytes) => _bytes = bytes;

    public static RazerReport Create(byte transactionId, byte commandClass, byte commandId, ReadOnlySpan<byte> arguments, byte dataSize)
    {
        if (arguments.Length > MaxArguments)
            throw new ArgumentException($"A report carries at most {MaxArguments} argument bytes.", nameof(arguments));
        var bytes = new byte[Size];
        bytes[StatusAt] = (byte)ReportStatus.New;
        bytes[TransactionAt] = transactionId;
        bytes[DataSizeAt] = dataSize;
        bytes[ClassAt] = commandClass;
        bytes[IdAt] = commandId;
        arguments.CopyTo(bytes.AsSpan(ArgumentsAt));
        bytes[ChecksumAt] = Checksum(bytes);
        return new RazerReport(bytes);
    }

    /// <summary>A command whose data size is just the arguments it carries.</summary>
    public static RazerReport Create(byte transactionId, byte commandClass, byte commandId, params byte[] arguments) =>
        Create(transactionId, commandClass, commandId, arguments, (byte)arguments.Length);

    public static RazerReport Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"A Razer report is {Size} bytes, got {bytes.Length}.", nameof(bytes));
        return new RazerReport(bytes.ToArray());
    }

    public ReportStatus Status => (ReportStatus)_bytes[StatusAt];
    public byte TransactionId => _bytes[TransactionAt];
    public int RemainingPackets => (_bytes[RemainingAt] << 8) | _bytes[RemainingAt + 1];
    public byte ProtocolType => _bytes[ProtocolAt];
    public byte DataSize => _bytes[DataSizeAt];
    public byte CommandClass => _bytes[ClassAt];
    public byte CommandId => _bytes[IdAt];
    public ReadOnlySpan<byte> Arguments => _bytes.AsSpan(ArgumentsAt, MaxArguments);
    public byte StoredChecksum => _bytes[ChecksumAt];
    public bool ChecksumValid => StoredChecksum == Checksum(_bytes);

    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>True when this is the device's answer to <paramref name="request"/>.</summary>
    public bool Answers(RazerReport request) =>
        CommandClass == request.CommandClass && CommandId == request.CommandId;

    /// <summary>XOR of bytes 2 to 87: everything after the status and transaction id up to the checksum.</summary>
    public static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte crc = 0;
        for (int i = RemainingAt; i < ChecksumAt; i++)
            crc ^= report[i];
        return crc;
    }
}
