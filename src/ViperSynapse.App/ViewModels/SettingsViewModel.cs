using CommunityToolkit.Mvvm.ComponentModel;
using ViperSynapse.App.Services;

namespace ViperSynapse.App.ViewModels;

/// <summary>The SETTINGS tab, as Synapse 4 lays it out: SYNAPSE (auto-launch, the tray, notifications) and GENERAL
/// (language, what's new, about). Start at sign-in is off unless the user turns it on.</summary>
public sealed partial class SettingsViewModel : TabViewModel
{
    readonly MainViewModel _main;
    AppPreferences _preferences;
    readonly bool _ready;
    readonly string _built;

    /// <param name="main">The window.</param>
    /// <param name="preferences">Read before the UI thread started.</param>
    /// <param name="startAtLogin">Whether the autostart entry exists, also read before the UI thread started.</param>
    /// <param name="built">When this build was made.</param>
    public SettingsViewModel(MainViewModel main, AppPreferences preferences, bool startAtLogin, string built = "")
    {
        _main = main;
        _preferences = preferences;
        _built = built;
        StartAtLogin = startAtLogin;
        StartMinimized = preferences.StartMinimized;
        CloseToTray = preferences.CloseToTray;
        Notifications = preferences.Notifications;
        _ready = true;
    }

    public override string Header => "SETTINGS";
    public override TabIcon Icon => TabIcon.Settings;

    public static IReadOnlyList<string> Pages { get; } = ["SYNAPSE", "GENERAL"];
    public static IReadOnlyList<string> Languages { get; } = ["English"];

    [ObservableProperty] public partial string SelectedPage { get; set; } = "SYNAPSE";
    [ObservableProperty] public partial bool StartAtLogin { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool Notifications { get; set; }
    [ObservableProperty] public partial string Language { get; set; } = "English";

    public bool ShowSynapse => SelectedPage == "SYNAPSE";
    public bool ShowGeneral => SelectedPage == "GENERAL";

    public string Version => $"Version {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}" + (_built.Length > 0 ? $"  ·  built {_built}" : "");

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSynapse));
        OnPropertyChanged(nameof(ShowGeneral));
    }

    partial void OnStartAtLoginChanged(bool value) => WriteAutostart();

    partial void OnStartMinimizedChanged(bool value)
    {
        Save(_preferences with { StartMinimized = value });
        WriteAutostart();
    }

    partial void OnCloseToTrayChanged(bool value) => Save(_preferences with { CloseToTray = value });

    partial void OnNotificationsChanged(bool value) => Save(_preferences with { Notifications = value });

    void WriteAutostart()
    {
        if (!_ready)
            return;
        _ = Autostart.SetAsync(StartAtLogin, StartMinimized).ContinueWith(t =>
        {
            if (t.Exception != null)
                _main.Fail("Could not change start at sign-in", t.Exception.GetBaseException());
        }, TaskScheduler.Default);
    }

    void Save(AppPreferences preferences)
    {
        if (!_ready)
            return;
        _preferences = preferences;
        _ = preferences.SaveAsync();
    }
}
