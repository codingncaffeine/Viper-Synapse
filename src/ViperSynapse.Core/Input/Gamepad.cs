namespace ViperSynapse.Core.Input;

/// <summary>A control of the virtual gamepad, laid out as an Xbox 360 pad (what games and SDL expect).</summary>
public enum GamepadControl
{
    LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
    RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
    LeftTrigger, RightTrigger,
    A, B, X, Y,
    LeftBumper, RightBumper,
    Back, Start, Guide,
    LeftStickClick, RightStickClick,
    DpadUp, DpadDown, DpadLeft, DpadRight,
}

/// <summary>One evdev event for the virtual gamepad.</summary>
public readonly record struct PadEvent(ushort Type, ushort Code, int Value);

/// <summary>
/// What the virtual gamepad holds, turned into evdev events with the codes the kernel's xpad driver uses. Keys
/// standing in for a stick add up: W and D together push it up and right, at full travel on each axis, as
/// Synapse does; W and S together cancel out. Not thread-safe: the caller serialises.
/// </summary>
public sealed class GamepadState
{
    public const ushort AbsX = 0x00, AbsY = 0x01, AbsZ = 0x02, AbsRx = 0x03, AbsRy = 0x04, AbsRz = 0x05, AbsHat0X = 0x10, AbsHat0Y = 0x11;
    public const int StickMax = 32767, TriggerMax = 255;

    /// <summary>The buttons, in xpad's codes (BTN_A ... BTN_THUMBR).</summary>
    public static readonly IReadOnlyDictionary<GamepadControl, ushort> Buttons = new Dictionary<GamepadControl, ushort>
    {
        [GamepadControl.A] = 0x130, [GamepadControl.B] = 0x131, [GamepadControl.X] = 0x133, [GamepadControl.Y] = 0x134,
        [GamepadControl.LeftBumper] = 0x136, [GamepadControl.RightBumper] = 0x137,
        [GamepadControl.Back] = 0x13A, [GamepadControl.Start] = 0x13B, [GamepadControl.Guide] = 0x13C,
        [GamepadControl.LeftStickClick] = 0x13D, [GamepadControl.RightStickClick] = 0x13E,
    };

    /// <summary>The axes and their ranges, for declaring the device.</summary>
    public static readonly IReadOnlyList<(ushort Axis, int Min, int Max)> Axes =
    [
        (AbsX, -32768, StickMax), (AbsY, -32768, StickMax), (AbsRx, -32768, StickMax), (AbsRy, -32768, StickMax),
        (AbsZ, 0, TriggerMax), (AbsRz, 0, TriggerMax), (AbsHat0X, -1, 1), (AbsHat0Y, -1, 1),
    ];

    const ushort EvKey = 0x01, EvAbs = 0x03;

    readonly Dictionary<GamepadControl, int> _held = [];
    readonly Dictionary<ushort, int> _axes = [];

    /// <summary>A key standing in for <paramref name="control"/> went down or up; returns the events to send (none if
    /// nothing changed). Two keys on one control count: the control lets go when the last one does.</summary>
    public IReadOnlyList<PadEvent> Set(GamepadControl control, bool down)
    {
        int count = _held.GetValueOrDefault(control) + (down ? 1 : -1);
        if (count <= 0) _held.Remove(control);
        else _held[control] = count;
        bool now = count > 0;
        if (now == (count - (down ? 1 : -1) > 0))
            return [];

        if (Buttons.TryGetValue(control, out var button))
            return [new(EvKey, button, now ? 1 : 0)];
        var (axis, value) = control switch
        {
            GamepadControl.LeftStickUp or GamepadControl.LeftStickDown => (AbsY, Direction(GamepadControl.LeftStickDown, GamepadControl.LeftStickUp) * StickMax),
            GamepadControl.LeftStickLeft or GamepadControl.LeftStickRight => (AbsX, Direction(GamepadControl.LeftStickRight, GamepadControl.LeftStickLeft) * StickMax),
            GamepadControl.RightStickUp or GamepadControl.RightStickDown => (AbsRy, Direction(GamepadControl.RightStickDown, GamepadControl.RightStickUp) * StickMax),
            GamepadControl.RightStickLeft or GamepadControl.RightStickRight => (AbsRx, Direction(GamepadControl.RightStickRight, GamepadControl.RightStickLeft) * StickMax),
            GamepadControl.LeftTrigger => (AbsZ, now ? TriggerMax : 0),
            GamepadControl.RightTrigger => (AbsRz, now ? TriggerMax : 0),
            GamepadControl.DpadUp or GamepadControl.DpadDown => (AbsHat0Y, Direction(GamepadControl.DpadDown, GamepadControl.DpadUp)),
            _ => (AbsHat0X, Direction(GamepadControl.DpadRight, GamepadControl.DpadLeft)),
        };
        if (_axes.GetValueOrDefault(axis) == value)
            return [];
        _axes[axis] = value;
        return [new(EvAbs, axis, value)];
    }

    // Down and right are positive, as on a real pad.
    int Direction(GamepadControl positive, GamepadControl negative) =>
        (_held.ContainsKey(positive) ? 1 : 0) - (_held.ContainsKey(negative) ? 1 : 0);

    public static string Label(GamepadControl control) => control switch
    {
        GamepadControl.LeftStickUp => "Left Stick Up",
        GamepadControl.LeftStickDown => "Left Stick Down",
        GamepadControl.LeftStickLeft => "Left Stick Left",
        GamepadControl.LeftStickRight => "Left Stick Right",
        GamepadControl.RightStickUp => "Right Stick Up",
        GamepadControl.RightStickDown => "Right Stick Down",
        GamepadControl.RightStickLeft => "Right Stick Left",
        GamepadControl.RightStickRight => "Right Stick Right",
        GamepadControl.LeftTrigger => "Left Trigger",
        GamepadControl.RightTrigger => "Right Trigger",
        GamepadControl.LeftBumper => "Left Bumper",
        GamepadControl.RightBumper => "Right Bumper",
        GamepadControl.LeftStickClick => "Left Stick Click",
        GamepadControl.RightStickClick => "Right Stick Click",
        GamepadControl.DpadUp => "D-Pad Up",
        GamepadControl.DpadDown => "D-Pad Down",
        GamepadControl.DpadLeft => "D-Pad Left",
        GamepadControl.DpadRight => "D-Pad Right",
        _ => control.ToString(),
    };
}
