using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Bindings;

/// <summary>Modifier keys a keyboard function holds around its key, as Synapse offers them.</summary>
[Flags]
public enum Modifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Super = 8,
}

/// <summary>The mouse functions Synapse lists under "Mouse Function".</summary>
public enum MouseFunction
{
    LeftClick,
    RightClick,
    ScrollClick,
    Back,
    Forward,
    DoubleClick,
    ScrollUp,
    ScrollDown,
    ScrollLeft,
    ScrollRight,
}

/// <summary>How a macro plays when its key is pressed.</summary>
public enum MacroPlayback
{
    /// <summary>Plays to the end once per press.</summary>
    Once,
    /// <summary>Plays a set number of times per press.</summary>
    Times,
    /// <summary>Repeats while the key is held, stopping when it is released.</summary>
    WhileHeld,
    /// <summary>A press starts repeating, the next press stops it.</summary>
    Toggle,
}

/// <summary>The sensitivity functions Synapse lists: step through the stages, or a clutch that holds a set DPI while
/// the key is down.</summary>
public enum SensitivityAction
{
    StageUp,
    StageDown,
    CycleUp,
    CycleDown,
    Clutch,
}

/// <summary>What a scroll-mode key does: switch between Tactile and Free-Spin (from Precision Tactile it goes to
/// Free-Spin), or set one mode.</summary>
public enum ScrollModeAction
{
    Toggle,
    Tactile,
    FreeSpin,
    PrecisionTactile,
}

public enum CycleDirection
{
    Next,
    Previous,
    Specific,
}

/// <summary>What one key does. Each subtype is one entry of Synapse's function rail.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DefaultBinding), "default")]
[JsonDerivedType(typeof(DisableBinding), "disable")]
[JsonDerivedType(typeof(KeyboardBinding), "keyboard")]
[JsonDerivedType(typeof(MouseBinding), "mouse")]
[JsonDerivedType(typeof(MacroBinding), "macro")]
[JsonDerivedType(typeof(HypershiftBinding), "hypershift")]
[JsonDerivedType(typeof(SwitchProfileBinding), "switch-profile")]
[JsonDerivedType(typeof(SwitchKeymapBinding), "switch-keymap")]
[JsonDerivedType(typeof(LaunchBinding), "launch")]
[JsonDerivedType(typeof(TextBinding), "text")]
[JsonDerivedType(typeof(SensitivityBinding), "sensitivity")]
[JsonDerivedType(typeof(ScrollModeBinding), "scroll-mode")]
[JsonDerivedType(typeof(Perfect180Binding), "perfect-180")]
[JsonDerivedType(typeof(GamepadBinding), "gamepad")]
public abstract record Binding;

/// <summary>The key does what it does with no software running.</summary>
public sealed record DefaultBinding : Binding
{
    public static DefaultBinding Instance { get; } = new();
}

public sealed record DisableBinding : Binding;

/// <summary>Sends one key (any Linux key code, media keys included), with modifiers held around it.
/// Turbo repeats it at <see cref="TurboRate"/> presses a second while the source key is held.</summary>
public sealed record KeyboardBinding(ushort Key, Modifiers Modifiers = Modifiers.None, bool Turbo = false, int TurboRate = 10) : Binding;

public sealed record MouseBinding(MouseFunction Function, bool Turbo = false, int TurboRate = 10) : Binding;

public sealed record MacroBinding(Guid MacroId, MacroPlayback Playback = MacroPlayback.Once, int Times = 1) : Binding;

/// <summary>While held, every key of the device uses its Hypershift layer.</summary>
public sealed record HypershiftBinding : Binding;

public sealed record SwitchProfileBinding(CycleDirection Direction, Guid? ProfileId = null) : Binding;

public sealed record SwitchKeymapBinding(CycleDirection Direction, int Keymap = 0) : Binding;

/// <summary>Starts a program: a command line, run without a shell.</summary>
public sealed record LaunchBinding(string Command) : Binding;

/// <summary>Types a piece of text.</summary>
public sealed record TextBinding(string Text) : Binding;

/// <summary>Works a control of the virtual gamepad (Synapse's Quick remapping and controller functions).</summary>
public sealed record GamepadBinding(Input.GamepadControl Control) : Binding;

/// <summary>Changes the mouse's sensitivity stage, or (Clutch) holds <see cref="Dpi"/> while the key is down.</summary>
public sealed record SensitivityBinding(SensitivityAction Action, int Dpi = 400) : Binding;

/// <summary>Changes the scroll wheel's mode.</summary>
public sealed record ScrollModeBinding(ScrollModeAction Action = ScrollModeAction.Toggle) : Binding;

/// <summary>Razer's Perfect 180: one press turns the view half way round by sending <see cref="Counts"/> of horizontal
/// mouse movement, spread over <see cref="DurationMs"/> (0: all at once). The count that makes exactly 180 degrees
/// depends on the game and its sensitivity (see <see cref="Perfect180"/>); <see cref="Game"/> and
/// <see cref="Sensitivity"/> are kept only so the editor can show how the count was worked out.</summary>
public sealed record Perfect180Binding(int Counts = 2000, bool Left = false, int DurationMs = 10, string Game = "", double Sensitivity = 0) : Binding;

/// <summary>The games whose turn per mouse count is well known: degrees = counts x sensitivity x yaw.</summary>
public static class Perfect180
{
    public sealed record GameYaw(string Name, double Yaw);

    public static IReadOnlyList<GameYaw> Games { get; } =
    [
        new("Counter-Strike 2 / CS:GO", 0.022),
        new("Apex Legends", 0.022),
        new("Team Fortress 2 / Source games", 0.022),
        new("Valorant", 0.07),
        new("Overwatch 2", 0.0066),
        new("Call of Duty (MW / Warzone)", 0.0066),
    ];

    public const int MaxCounts = 200000;

    /// <summary>The counts that turn 180 degrees at this sensitivity in a game with this yaw.</summary>
    public static int CountsFor(double yaw, double sensitivity) =>
        yaw <= 0 || sensitivity <= 0 ? 0 : (int)Math.Clamp(Math.Round(180.0 / (yaw * sensitivity)), 1, MaxCounts);

    /// <summary>The movement of each step: <paramref name="total"/> split into <paramref name="steps"/> parts that add
    /// up to it exactly.</summary>
    public static IEnumerable<int> Steps(int total, int steps)
    {
        steps = Math.Max(1, steps);
        int sent = 0;
        for (int i = 1; i <= steps; i++)
        {
            int upTo = (int)Math.Round((double)total * i / steps);
            yield return upTo - sent;
            sent = upTo;
        }
    }
}
