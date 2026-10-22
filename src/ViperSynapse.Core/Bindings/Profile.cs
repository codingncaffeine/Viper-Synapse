using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Core.Bindings;

/// <summary>Source codes for controls that do not send a key of their own: wheel directions. Real keys and
/// buttons use their Linux key code.</summary>
public static class SourceKeys
{
    public const ushort WheelUp = 0x1001, WheelDown = 0x1002, WheelLeft = 0x1003, WheelRight = 0x1004;

    /// <summary>Thumbstick directions and click, in 4-way and 8-way mode: each is a control of its own.</summary>
    public const ushort StickUp = 0x1011, StickDown = 0x1012, StickLeft = 0x1013, StickRight = 0x1014, StickClick = 0x1015;

    public static bool IsWheel(ushort code) => code is >= WheelUp and <= WheelRight;

    public static bool IsStick(ushort code) => code is >= StickUp and <= StickClick;
}

/// <summary>One set of bindings: the Standard layer and the Hypershift layer. A key missing from a layer
/// keeps its default; a key missing from Hypershift falls back to its Standard binding.</summary>
public sealed record Keymap(string Name, IReadOnlyDictionary<ushort, Binding> Standard, IReadOnlyDictionary<ushort, Binding> Hypershift)
{
    public static Keymap Empty(string name) => new(name, new Dictionary<ushort, Binding>(), new Dictionary<ushort, Binding>());

    public Binding Resolve(ushort source, bool hypershift)
    {
        if (hypershift && Hypershift.TryGetValue(source, out var shifted))
            return shifted;
        return Standard.TryGetValue(source, out var binding) ? binding : DefaultBinding.Instance;
    }

    public Keymap With(ushort source, Binding binding, bool hypershift = false)
    {
        var layer = new Dictionary<ushort, Binding>(hypershift ? Hypershift : Standard) { [source] = binding };
        return hypershift ? this with { Hypershift = layer } : this with { Standard = layer };
    }
}

/// <summary>How the thumbstick reports: as the analog stick of the keypad's gamepad, or as four or eight directions
/// that act like keys.</summary>
public enum ThumbstickMode
{
    Analog,
    FourWay,
    EightWay,
}

/// <summary>What stops a running macro: only its own key (as each playback mode says), or any key press.</summary>
public enum MacroInterruption
{
    DedicatedKey,
    AnyKey,
}

/// <summary>A device profile: its keymaps (the Tartarus switches between up to eight), the programs that bring it
/// forward, and its lighting, sensitivity and power settings. A null setting leaves the device as it is.</summary>
public sealed record Profile(
    Guid Id,
    string Name,
    IReadOnlyList<Keymap> Keymaps,
    IReadOnlyList<string> LinkedPrograms,
    LightingSettings? Lighting = null,
    PerformanceSettings? Performance = null,
    PowerSettings? Power = null,
    CalibrationSettings? Calibration = null,
    ThumbstickMode Thumbstick = ThumbstickMode.Analog,
    MacroInterruption Interruption = MacroInterruption.DedicatedKey)
{
    public const int MaxKeymaps = 8;

    public static Profile Create(string name) => new(Guid.NewGuid(), name, [Keymap.Empty("Keymap 1")], []);
}
