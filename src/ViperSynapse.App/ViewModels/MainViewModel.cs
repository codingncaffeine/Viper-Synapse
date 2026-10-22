using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.App.ViewModels;

/// <summary>How a tab's icon is drawn: Synapse 4 puts most in a green disc, Settings is a plain gear.</summary>
public enum TabIcon
{
    Synapse,
    Keypad,
    Mouse,
    Keyboard,
    Device,
    LinkedGames,
    Macro,
    Settings,
}

/// <summary>One tab of the top bar: SYNAPSE, a device, LINKED GAMES, MACRO or SETTINGS.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    public abstract string Header { get; }

    /// <summary>The page title centred in the bar under the tabs.</summary>
    public virtual string Title => Header;

    public abstract TabIcon Icon { get; }
}

/// <summary>
/// The window: its top-bar tabs, back/forward through them, and the connection to the service. Results of service
/// calls arrive on pool threads and are applied on the UI thread; nothing here waits on the service from the UI
/// thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly DaemonClient _client;
    readonly List<TabViewModel> _back = [], _forward = [];
    bool _navigating;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public HomeViewModel Home { get; }
    public LinkedGamesViewModel LinkedGames { get; }
    public MacrosViewModel Macros { get; }
    public SettingsViewModel Settings { get; }

    public string Title => SelectedTab?.Title ?? "";
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public MainViewModel(DaemonClient client, AppPreferences preferences, bool startAtLogin, string built = "")
    {
        _client = client;
        Settings = new SettingsViewModel(this, preferences, startAtLogin, built);
        Home = new HomeViewModel(this);
        LinkedGames = new LinkedGamesViewModel(this);
        Macros = new MacrosViewModel(client, this);
        Tabs.Add(Home);
        Tabs.Add(LinkedGames);
        Tabs.Add(Macros);
        Tabs.Add(Settings);
        SelectedTab = Home;
    }

    public DaemonClient Client => _client;

    public MacroLibrary MacroLibrary => Macros.Library;

    public IEnumerable<DeviceViewModel> Devices => Tabs.OfType<DeviceViewModel>();

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (!_navigating && oldValue != null && newValue != null && oldValue != newValue)
        {
            _back.Add(oldValue);
            _forward.Clear();
        }
        (newValue as DeviceViewModel)?.Shown();
        if (newValue == LinkedGames)
            LinkedGames.Refresh();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    [RelayCommand]
    void GoBack() => Step(_back, _forward);

    [RelayCommand]
    void GoForward() => Step(_forward, _back);

    void Step(List<TabViewModel> from, List<TabViewModel> to)
    {
        while (from.Count > 0)
        {
            var tab = from[^1];
            from.RemoveAt(from.Count - 1);
            if (!Tabs.Contains(tab))
                continue;   // a device that has been unplugged since
            if (SelectedTab != null)
                to.Add(SelectedTab);
            _navigating = true;
            SelectedTab = tab;
            _navigating = false;
            return;
        }
    }

    /// <summary>Reads devices, settings and macros from the service again.</summary>
    [RelayCommand]
    async Task Refresh()
    {
        try
        {
            await Macros.LoadAsync();
            await RefreshDevicesAsync();
            foreach (var device in Devices.ToList())
                await device.ReloadAsync();
            Problem = null;
        }
        catch (Exception e)
        {
            Fail("Could not refresh", e);
        }
    }

    public async Task StartAsync()
    {
        try
        {
            _client.DevicesChanged += () => _ = RefreshDevicesAsync();
            _client.StickMoved += (key, x, y) => Dispatcher.UIThread.Post(() =>
            {
                foreach (var device in Devices.Where(d => d.Key == key))
                    device.StickAt(x, y);
            });
            _client.SettingsChanged += key => Dispatcher.UIThread.Post(() =>
            {
                foreach (var device in Devices.Where(d => d.Key == key))
                    _ = device.ReloadAsync();
            });
            await _client.ConnectAsync().ConfigureAwait(false);
            await Macros.LoadAsync().ConfigureAwait(false);
            await RefreshDevicesAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Dispatcher.UIThread.Post(() => Problem = "Cannot reach the Viper Synapse service: " + e.Message);
        }
    }

    async Task RefreshDevicesAsync()
    {
        var devices = await _client.GetDevicesAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var current = Devices.ToList();
            // One tab per device model; a second identical device shares the tab and the settings.
            foreach (var info in devices.GroupBy(d => d.Key).Select(g => g.First()))
            {
                var tab = current.FirstOrDefault(t => t.Key == info.Key);
                if (tab != null)
                {
                    tab.Info = info;
                    current.Remove(tab);
                    continue;
                }
                tab = new DeviceViewModel(info, this);
                // Devices sit between SYNAPSE and LINKED GAMES, as in Synapse.
                Tabs.Insert(Tabs.IndexOf(LinkedGames), tab);
                _ = tab.ReloadAsync();
            }
            foreach (var gone in current)
            {
                if (SelectedTab == gone)
                    SelectedTab = Home;
                Tabs.Remove(gone);
            }
            Home.Devices.Clear();
            foreach (var tab in Devices)
                Home.Devices.Add(tab);
            OpenRequested();
        });
    }

    /// <summary>For screenshots and checks: VIPER_SYNAPSE_OPEN=02eb opens a device tab, 02eb/LIGHTING a page of it,
    /// 02eb:5 also opens key 5's flyout; macros, settings, games and home open those tabs. Applied once, when the
    /// devices first arrive.</summary>
    void OpenRequested()
    {
        if (_opened || Environment.GetEnvironmentVariable("VIPER_SYNAPSE_OPEN") is not { Length: > 0 } open)
            return;
        _opened = true;
        TabViewModel? fixedTab = open switch
        {
            "macros" => Macros,
            "settings" => Settings,
            "games" => LinkedGames,
            "home" => Home,
            _ => null,
        };
        if (fixedTab != null)
        {
            SelectedTab = fixedTab;
            return;
        }
        var parts = open.Split(':');
        var target = parts[0].Split('/');
        if (Devices.FirstOrDefault(d => d.Key == target[0]) is not { } device)
            return;
        SelectedTab = device;
        if (target.Length > 1 && device.Pages.Contains(target[1]))
            device.SelectedPage = target[1];
        if (parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var number))
            Dispatcher.UIThread.Post(() => device.SelectedKey = device.Rows.FirstOrDefault(r => r.Key.Number == number), DispatcherPriority.Background);
    }

    bool _opened;

    public void Show(TabViewModel tab) => SelectedTab = tab;

    /// <summary>Reports a failed service call where the user can see it.</summary>
    public void Fail(string what, Exception e) => Dispatcher.UIThread.Post(() => Problem = $"{what}: {e.Message}");
}

/// <summary>The SYNAPSE tab: the dashboard of connected devices and modules.</summary>
public sealed partial class HomeViewModel(MainViewModel main) : TabViewModel
{
    public override string Header => "SYNAPSE";
    public override string Title => "VIPER SYNAPSE";
    public override TabIcon Icon => TabIcon.Synapse;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public static IReadOnlyList<string> Pages { get; } = ["DASHBOARD", "DEVICES & MODULES"];

    [ObservableProperty] public partial string SelectedPage { get; set; } = "DASHBOARD";

    public bool ShowDashboard => SelectedPage == "DASHBOARD";
    public bool ShowModules => SelectedPage == "DEVICES & MODULES";

    public string Version => typeof(HomeViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowModules));
    }

    public void Open(DeviceViewModel device) => main.Show(device);
    public void OpenLinkedGames() => main.Show(main.LinkedGames);
    public void OpenMacros() => main.Show(main.Macros);
    public void OpenSettings() => main.Show(main.Settings);
}
