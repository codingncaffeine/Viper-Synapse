using ViperSynapse.App.Services;

namespace ViperSynapse.Tests;

/// <summary>Start at sign-in: off unless turned on, a valid desktop entry when on, gone again when off.
/// Runs against a throwaway XDG_CONFIG_HOME, never the real one.</summary>
[Collection("environment")]
public class AutostartTests : IDisposable
{
    readonly string _config = Path.Combine(Path.GetTempPath(), "viper-synapse-autostart-" + Guid.NewGuid().ToString("N"));
    readonly string? _saved = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public AutostartTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _saved);
        if (Directory.Exists(_config))
            Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task Start_at_sign_in_is_off_until_turned_on_and_gone_when_turned_off()
    {
        Assert.False(Autostart.IsEnabled);

        await Autostart.SetAsync(true);
        var entry = Path.Combine(_config, "autostart", "viper-synapse.desktop");
        Assert.True(Autostart.IsEnabled);
        var text = await File.ReadAllTextAsync(entry, TestContext.Current.CancellationToken);
        Assert.StartsWith("[Desktop Entry]", text);
        Assert.Contains("Type=Application", text);
        Assert.Matches(@"(?m)^Exec=.+ --background$", text);

        await Autostart.SetAsync(false);
        Assert.False(Autostart.IsEnabled);
        Assert.False(File.Exists(entry));
    }

    [Fact]
    public void Preferences_default_to_keeping_the_service_in_the_tray()
    {
        Assert.True(AppPreferences.Load().CloseToTray);
    }
}
