using ViperSynapse.Core.Input;

namespace ViperSynapse.Core.Bindings;

/// <summary>How a binding reads in the key list: a small category caption over the value, as Synapse shows
/// "KEYBOARD FUNCTION" over "Ctrl + C".</summary>
public static class BindingText
{
    public static (string Category, string Value) Describe(Binding binding, ushort defaultCode, Func<Guid, string?>? macroName = null) => binding switch
    {
        DefaultBinding when KeyCodes.IsMouseButton(defaultCode) => ("MOUSE FUNCTION", KeyLabel(defaultCode)),
        DefaultBinding when defaultCode != 0 => ("KEYBOARD FUNCTION", KeyLabel(defaultCode)),
        DefaultBinding => ("DEFAULT", "Default"),
        DisableBinding => ("DISABLE", "Disable"),
        KeyboardBinding k => ("KEYBOARD FUNCTION", Chord(k.Modifiers, k.Key) + (k.Turbo ? $" (Turbo {k.TurboRate}/s)" : "")),
        MouseBinding m => ("MOUSE FUNCTION", MouseLabel(m.Function) + (m.Turbo ? $" (Turbo {m.TurboRate}/s)" : "")),
        MacroBinding m => ("MACRO", macroName?.Invoke(m.MacroId) ?? "Missing macro"),
        HypershiftBinding => ("RAZER HYPERSHIFT", "Hypershift"),
        SwitchProfileBinding p => ("SWITCH PROFILE", p.Direction switch
        {
            CycleDirection.Next => "Cycle Up Profile",
            CycleDirection.Previous => "Cycle Down Profile",
            _ => "Switch Profile",
        }),
        SwitchKeymapBinding k => ("SWITCH KEYMAP", k.Direction switch
        {
            CycleDirection.Next => "Cycle Up Keymap",
            CycleDirection.Previous => "Cycle Down Keymap",
            _ => $"Keymap {k.Keymap + 1}",
        }),
        LaunchBinding l => ("LAUNCH PROGRAM", ProgramName(l.Command)),
        TextBinding t => ("TEXT FUNCTION", t.Text.Length > 24 ? t.Text[..24] + "…" : t.Text),
        GamepadBinding g => ("GAMEPAD FUNCTION", GamepadState.Label(g.Control)),
        _ => ("", ""),
    };

    public static string Chord(Modifiers modifiers, ushort key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Modifiers.Ctrl)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Modifiers.Super)) parts.Add("Super");
        parts.Add(KeyLabel(key));
        return string.Join(" + ", parts);
    }

    /// <summary>A key's name as printed on a keycap: "Caps Lock", not KEY_CAPSLOCK.</summary>
    public static string KeyLabel(ushort code)
    {
        string name = KeyCodes.Name(code);
        if (Special.TryGetValue(name, out var special))
            return special;
        if (name.StartsWith("KEY_", StringComparison.Ordinal))
            name = name[4..];
        else if (name.StartsWith("BTN_", StringComparison.Ordinal))
            name = "Button " + name[4..];
        if (name.Length == 1)
            return name;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 2 && w.Any(char.IsDigit) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }

    static readonly Dictionary<string, string> Special = new()
    {
        ["KEY_GRAVE"] = "~", ["KEY_MINUS"] = "-", ["KEY_EQUAL"] = "=", ["KEY_LEFTBRACE"] = "[", ["KEY_RIGHTBRACE"] = "]",
        ["KEY_BACKSLASH"] = "\\", ["KEY_SEMICOLON"] = ";", ["KEY_APOSTROPHE"] = "'", ["KEY_COMMA"] = ",", ["KEY_DOT"] = ".",
        ["KEY_SLASH"] = "/", ["KEY_ESC"] = "Esc", ["KEY_CAPSLOCK"] = "Caps Lock", ["KEY_LEFTSHIFT"] = "Shift",
        ["KEY_RIGHTSHIFT"] = "Right Shift", ["KEY_LEFTCTRL"] = "Ctrl", ["KEY_RIGHTCTRL"] = "Right Ctrl", ["KEY_LEFTALT"] = "Alt",
        ["KEY_RIGHTALT"] = "Right Alt", ["KEY_LEFTMETA"] = "Super", ["KEY_RIGHTMETA"] = "Right Super", ["KEY_PAGEUP"] = "Page Up",
        ["KEY_PAGEDOWN"] = "Page Down", ["KEY_SYSRQ"] = "Print Screen", ["KEY_SCROLLLOCK"] = "Scroll Lock",
        ["KEY_NUMLOCK"] = "Num Lock", ["KEY_KPENTER"] = "Numpad Enter", ["KEY_VOLUMEUP"] = "Volume Up",
        ["KEY_VOLUMEDOWN"] = "Volume Down", ["KEY_MUTE"] = "Mute", ["KEY_PLAYPAUSE"] = "Play / Pause",
        ["KEY_NEXTSONG"] = "Next Track", ["KEY_PREVIOUSSONG"] = "Previous Track", ["KEY_STOPCD"] = "Stop",
        ["KEY_MICMUTE"] = "Mic Mute", ["BTN_LEFT"] = "Left Click", ["BTN_RIGHT"] = "Right Click",
        ["BTN_MIDDLE"] = "Scroll Click", ["BTN_SIDE"] = "Mouse Button 4", ["BTN_EXTRA"] = "Mouse Button 5",
    };

    public static string MouseLabel(MouseFunction f) => f switch
    {
        MouseFunction.LeftClick => "Left Click",
        MouseFunction.RightClick => "Right Click",
        MouseFunction.ScrollClick => "Scroll Click",
        MouseFunction.Back => "Mouse Button 4",
        MouseFunction.Forward => "Mouse Button 5",
        MouseFunction.DoubleClick => "Double Click",
        MouseFunction.ScrollUp => "Scroll Up",
        MouseFunction.ScrollDown => "Scroll Down",
        MouseFunction.ScrollLeft => "Scroll Left",
        MouseFunction.ScrollRight => "Scroll Right",
        _ => f.ToString(),
    };

    static string ProgramName(string command)
    {
        var first = command.Trim().Split(' ', 2)[0].Trim('"', '\'');
        return Path.GetFileName(first) is { Length: > 0 } name ? name : command;
    }
}
