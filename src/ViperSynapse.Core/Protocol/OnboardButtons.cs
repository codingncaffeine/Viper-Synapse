namespace ViperSynapse.Core.Protocol;

/// <summary>
/// The functions a mouse keeps in its own memory for each button (what Synapse on Windows wrote there),
/// read with 0x02/0x8C (profile, button id, layer). The answer echoes profile, button and layer, then the
/// function: its type, the length of its data, and the data.
///   01 mouse button (1 left, 2 right, 3 middle, 4 back, 5 forward, 9/0A wheel up/down, 68/69 wheel left/right)
///   02 keyboard key (modifier bits, HID usage)
///   06 sensitivity (06 cycle up), 0E repeated mouse button (button, rate), 12 profile (01 cycle up), 00 nothing
/// </summary>
public static class OnboardButtons
{
    /// <summary>The read for one button on one layer (0 standard, 1 Hypershift) of the stored profile.</summary>
    public static RazerReport Get(byte tid, byte buttonId, bool hypershift) =>
        RazerReport.Create(tid, 0x02, 0x8C, new byte[] { 0x01, buttonId, hypershift ? (byte)0x01 : (byte)0x00, 0, 0, 0, 0, 0, 0, 0 }, 0x0A);

    /// <summary>The function in an answer to <see cref="Get"/>, in words; null when the answer is not about that
    /// button (a stale reply) or its function is not one we know.</summary>
    public static string? Describe(RazerReport answer, byte buttonId)
    {
        var a = answer.Arguments.ToArray();
        if (a.Length < 5 || a[1] != buttonId)
            return null;
        byte type = a[3], length = a[4];
        if (5 + length > a.Length)
            return null;
        var data = a.Skip(5).Take(length).ToArray();
        return type switch
        {
            0x00 => "Disabled",
            0x01 when data.Length >= 1 => MouseButton(data[0]),
            0x02 when data.Length >= 2 => Key(data[0], data[1]),
            0x06 when data.Length >= 1 => data[0] switch { 0x06 => "Cycle Up Sensitivity Stages", 0x07 => "Cycle Down Sensitivity Stages", _ => "Sensitivity" },
            0x0E when data.Length >= 1 => "Repeat " + MouseButton(data[0]),
            0x12 when data.Length >= 1 => data[0] switch { 0x01 => "Cycle Up Profile", 0x02 => "Cycle Down Profile", _ => "Switch Profile" },
            _ => null,
        };
    }

    static string MouseButton(byte b) => b switch
    {
        0x01 => "Left Click",
        0x02 => "Right Click",
        0x03 => "Scroll Click",
        0x04 => "Mouse Button 4",
        0x05 => "Mouse Button 5",
        0x09 => "Scroll Up",
        0x0A => "Scroll Down",
        0x68 => "Scroll Left",
        0x69 => "Scroll Right",
        _ => $"Mouse Button {b}",
    };

    static string Key(byte modifiers, byte usage)
    {
        var parts = new List<string>();
        if ((modifiers & 0x11) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x22) != 0) parts.Add("Shift");
        if ((modifiers & 0x44) != 0) parts.Add("Alt");
        if ((modifiers & 0x88) != 0) parts.Add("Super");
        parts.Add(UsageName(usage));
        return string.Join(" + ", parts);
    }

    /// <summary>A HID keyboard usage's name.</summary>
    public static string UsageName(byte u) => u switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + u - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + u - 0x1E)).ToString(),
        0x27 => "0",
        0x28 => "Enter",
        0x29 => "Esc",
        0x2A => "Backspace",
        0x2B => "Tab",
        0x2C => "Space",
        0x2D => "-",
        0x2E => "=",
        0x2F => "[",
        0x30 => "]",
        0x31 => "\\",
        0x33 => ";",
        0x34 => "'",
        0x35 => "`",
        0x36 => ",",
        0x37 => ".",
        0x38 => "/",
        0x39 => "Caps Lock",
        >= 0x3A and <= 0x45 => $"F{u - 0x3A + 1}",
        0x46 => "Print Screen",
        0x47 => "Scroll Lock",
        0x48 => "Pause",
        0x49 => "Insert",
        0x4A => "Home",
        0x4B => "Page Up",
        0x4C => "Delete",
        0x4D => "End",
        0x4E => "Page Down",
        0x4F => "Right",
        0x50 => "Left",
        0x51 => "Down",
        0x52 => "Up",
        0x53 => "Num Lock",
        0x54 => "Keypad /",
        0x55 => "Keypad *",
        0x56 => "Keypad -",
        0x57 => "Keypad +",
        0x58 => "Keypad Enter",
        >= 0x59 and <= 0x61 => $"Keypad {u - 0x59 + 1}",
        0x62 => "Keypad 0",
        0x63 => "Keypad .",
        >= 0x68 and <= 0x73 => $"F{u - 0x68 + 13}",
        0xE0 => "Left Ctrl",
        0xE1 => "Left Shift",
        0xE2 => "Left Alt",
        0xE3 => "Left Super",
        _ => $"Key 0x{u:X2}",
    };
}
