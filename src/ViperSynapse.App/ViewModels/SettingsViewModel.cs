using CommunityToolkit.Mvvm.ComponentModel;
using ViperSynapse.App.Services;

namespace ViperSynapse.App.ViewModels;

/// <summary>The settings page behind the gear: start at sign-in (off by default), the tray, and About.</summary>
public sealed partial class SettingsViewModel : TabViewModel
{
    readonly MainViewModel _main;
    AppPreferences _preferences;
    readonly bool _ready;

    /// <param name="main">The window.</param>
    /// <param name="preferences">Read before the UI thread started.</param>
    /// <param name="startAtLogin">Whether the autostart entry exists, also read before the UI thread started.</param>
    /// <param name="built">When this build was made.</param>
    public SettingsViewModel(MainViewModel main, AppPreferences preferences, bool startAtLogin, string built = "")
    {
        _built = built;
        _main = main;
        _preferences = preferences;
        StartAtLogin = startAtLogin;
        CloseToTray = preferences.CloseToTray;
        _ready = true;
    }

    public override string Header => "SETTINGS";

    readonly string _built;

    public string Version => $"Viper Synapse {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}" + (_built.Length > 0 ? $"  ·  built {_built}" : "");

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (!_ready)
            return;
        _ = Autostart.SetAsync(value).ContinueWith(t =>
        {
            if (t.Exception != null)
                _main.Fail("Could not change start at sign-in", t.Exception.GetBaseException());
        }, TaskScheduler.Default);
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_ready)
            return;
        _preferences = _preferences with { CloseToTray = value };
        _ = _preferences.SaveAsync();
    }
}
