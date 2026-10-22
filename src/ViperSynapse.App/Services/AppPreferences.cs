using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViperSynapse.App.Services;

/// <summary>The window's own preferences (device settings live in the service). Stored in
/// $XDG_CONFIG_HOME/viper-synapse/app.json.</summary>
public sealed record AppPreferences(bool CloseToTray = true)
{
    static string FilePath => Path.Combine(Autostart.ConfigHome(), "viper-synapse", "app.json");

    /// <summary>Read before the UI thread starts; a missing or unreadable file gives the defaults.</summary>
    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), PreferencesJson.Default.AppPreferences) ?? new()
                : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public Task SaveAsync() => Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, PreferencesJson.Default.AppPreferences));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save preferences: {e.Message}");
        }
    });
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppPreferences))]
internal sealed partial class PreferencesJson : JsonSerializerContext;

/// <summary>
/// Start at sign-in, through the freedesktop autostart folder every Linux desktop reads
/// ($XDG_CONFIG_HOME/autostart). Off unless the user turns it on: off means no file at all. When on, the window
/// starts hidden in the tray with --background, so bindings and macros work from sign-in.
/// </summary>
public static class Autostart
{
    public static string ConfigHome()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrEmpty(config)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : config;
    }

    static string EntryPath => Path.Combine(ConfigHome(), "autostart", "viper-synapse.desktop");

    public static bool IsEnabled => File.Exists(EntryPath);

    /// <summary>The program to start: the installed launcher when there is one, else this very binary.</summary>
    static string Program()
    {
        var self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "viper-synapse");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "viper-synapse");
            if (File.Exists(candidate) && Path.GetFullPath(candidate) != Path.GetFullPath(self))
                return candidate;
        }
        return self;
    }

    public static Task SetAsync(bool enabled) => Task.Run(() =>
    {
        if (!enabled)
        {
            File.Delete(EntryPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        var program = Program();
        File.WriteAllText(EntryPath, $"""
            [Desktop Entry]
            Type=Application
            Name=Viper Synapse
            Comment=Razer device settings, key bindings and macros
            Exec={(program.Contains(' ') ? $"\"{program}\"" : program)} --background
            Icon=viper-synapse
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """);
    });
}
