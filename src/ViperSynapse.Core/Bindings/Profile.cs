using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Core.Bindings;

/// <summary>Source codes for controls that do not send a key of their own: wheel directions. Real keys and
/// buttons use their Linux key code.</summary>
public static class SourceKeys
{
    public const ushort WheelUp = 0x1001, WheelDown = 0x1002, WheelLeft = 0x1003, WheelRight = 0x1004;

    public static bool IsWheel(ushort code) => code is >= WheelUp and <= WheelRight;
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

/// <summary>A device profile: its keymaps (the Tartarus switches between up to eight), the programs that bring it
/// forward, and its lighting, sensitivity and power settings. A null setting leaves the device as it is.</summary>
public sealed record Profile(
    Guid Id,
    string Name,
    IReadOnlyList<Keymap> Keymaps,
    IReadOnlyList<string> LinkedPrograms,
    LightingSettings? Lighting = null,
    PerformanceSettings? Performance = null,
    PowerSettings? Power = null)
{
    public const int MaxKeymaps = 8;

    public static Profile Create(string name) => new(Guid.NewGuid(), name, [Keymap.Empty("Keymap 1")], []);
}
