using ViperSynapse.Core.Bindings;

namespace ViperSynapse.Service;

/// <summary>
/// Settings on disk under $XDG_CONFIG_HOME/viper-synapse: one file per device model
/// (devices/02eb.json), the macro library (macros.json) and the global shortcuts (shortcuts.json). Writes go to a temporary file that is renamed
/// over the old one, so a crash never leaves half a file. A file that cannot be read is set aside as
/// *.broken and replaced with defaults rather than stopping the service.
/// </summary>
public sealed class SettingsStore(string root)
{
    readonly Lock _gate = new();

    public string Root { get; } = root;

    public static string DefaultRoot()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "viper-synapse");
    }

    string DevicePath(string deviceKey) => Path.Combine(Root, "devices", deviceKey + ".json");
    string MacrosPath => Path.Combine(Root, "macros.json");

    /// <summary>A device seen for the first time gets default settings, saved at once so their ids stay put.</summary>
    public DeviceSettings LoadDevice(string deviceKey) => Load(DevicePath(deviceKey), SettingsJson.ReadSettings, () =>
    {
        var created = DeviceSettings.Create();
        Save(DevicePath(deviceKey), SettingsJson.Write(created));
        return created;
    });

    public void SaveDevice(string deviceKey, DeviceSettings settings) => Save(DevicePath(deviceKey), SettingsJson.Write(settings));

    public MacroLibrary LoadMacros() => Load(MacrosPath, SettingsJson.ReadMacros, () => MacroLibrary.Empty);

    public void SaveMacros(MacroLibrary macros) => Save(MacrosPath, SettingsJson.Write(macros));

    string ShortcutsPath => Path.Combine(Root, "shortcuts.json");

    public ShortcutList LoadShortcuts() => Load(ShortcutsPath, SettingsJson.ReadShortcuts, () => ShortcutList.Empty);

    public void SaveShortcuts(ShortcutList shortcuts) => Save(ShortcutsPath, SettingsJson.Write(shortcuts));

    T Load<T>(string path, Func<string, T> parse, Func<T> fallback)
    {
        lock (_gate)
        {
            if (!File.Exists(path))
                return fallback();
            try
            {
                return parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"{path}: unreadable ({e.Message}); kept as {path}.broken, starting from defaults.");
                File.Move(path, path + ".broken", overwrite: true);
                return fallback();
            }
        }
    }

    void Save(string path, string json)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }
}
