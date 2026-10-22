using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Bindings;

/// <summary>Everything the user set for one device model: its profiles and which profile and keymap are in use.</summary>
public sealed record DeviceSettings(IReadOnlyList<Profile> Profiles, Guid ActiveProfile, int ActiveKeymap)
{
    public static DeviceSettings Create()
    {
        var profile = Profile.Create("Default");
        return new DeviceSettings([profile], profile.Id, 0);
    }

    public Profile Active => Profiles.FirstOrDefault(p => p.Id == ActiveProfile) ?? Profiles[0];

    public Keymap ActiveKeymapOrFirst =>
        Active.Keymaps.Count == 0 ? Keymap.Empty("Keymap 1") : Active.Keymaps[Math.Clamp(ActiveKeymap, 0, Active.Keymaps.Count - 1)];

    /// <summary>The next or previous profile, wrapping around, or a named one.</summary>
    public DeviceSettings SwitchProfile(CycleDirection direction, Guid? id)
    {
        int at = Math.Max(0, Profiles.ToList().FindIndex(p => p.Id == ActiveProfile));
        var target = direction switch
        {
            CycleDirection.Next => Profiles[(at + 1) % Profiles.Count],
            CycleDirection.Previous => Profiles[(at - 1 + Profiles.Count) % Profiles.Count],
            _ => Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles[at],
        };
        return this with { ActiveProfile = target.Id, ActiveKeymap = 0 };
    }

    public DeviceSettings SwitchKeymap(CycleDirection direction, int keymap)
    {
        int count = Math.Max(1, Active.Keymaps.Count);
        int next = direction switch
        {
            CycleDirection.Next => (ActiveKeymap + 1) % count,
            CycleDirection.Previous => (ActiveKeymap - 1 + count) % count,
            _ => Math.Clamp(keymap, 0, count - 1),
        };
        return this with { ActiveKeymap = next };
    }
}

/// <summary>The macro library, shared by every device.</summary>
public sealed record MacroLibrary(IReadOnlyList<Macro> Macros)
{
    public static MacroLibrary Empty { get; } = new([]);

    public Macro? Find(Guid id) => Macros.FirstOrDefault(m => m.Id == id);
}

/// <summary>The JSON form of settings and macros: what is stored on disk and what crosses D-Bus.</summary>
public static class SettingsJson
{
    public static string Write(DeviceSettings settings) => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.DeviceSettings);
    public static DeviceSettings ReadSettings(string json) =>
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DeviceSettings) ?? throw new InvalidDataException("Empty device settings.");

    public static string Write(MacroLibrary macros) => JsonSerializer.Serialize(macros, SettingsJsonContext.Default.MacroLibrary);
    public static MacroLibrary ReadMacros(string json) =>
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MacroLibrary) ?? throw new InvalidDataException("Empty macro library.");

    public static string Write(IReadOnlyList<MacroStep> steps) => JsonSerializer.Serialize(steps, SettingsJsonContext.Default.IReadOnlyListMacroStep);
    public static IReadOnlyList<MacroStep> ReadSteps(string json) =>
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.IReadOnlyListMacroStep) ?? [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeviceSettings))]
[JsonSerializable(typeof(MacroLibrary))]
[JsonSerializable(typeof(IReadOnlyList<MacroStep>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
