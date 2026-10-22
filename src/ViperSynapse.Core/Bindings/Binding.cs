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
