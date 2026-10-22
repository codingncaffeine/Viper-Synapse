using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.App.ViewModels;

/// <summary>One tab of the top bar: SYNAPSE, a device, or MACROS.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    public abstract string Header { get; }
}

/// <summary>
/// The window: its top-bar tabs and the connection to the service. Results of service calls arrive on pool
/// threads and are applied on the UI thread; nothing here waits on the service from the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly DaemonClient _client;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public HomeViewModel Home { get; }
    public MacrosViewModel Macros { get; }

    public MainViewModel(DaemonClient client)
    {
        _client = client;
        Home = new HomeViewModel(this);
        Macros = new MacrosViewModel(client, this);
        Tabs.Add(Home);
        Tabs.Add(Macros);
        SelectedTab = Home;
    }

    public DaemonClient Client => _client;

    public MacroLibrary MacroLibrary => Macros.Library;

    public async Task StartAsync()
    {
        try
        {
            _client.DevicesChanged += () => _ = RefreshDevicesAsync();
            _client.SettingsChanged += key => Dispatcher.UIThread.Post(() =>
            {
                foreach (var device in Tabs.OfType<DeviceViewModel>().Where(d => d.Key == key))
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
            var current = Tabs.OfType<DeviceViewModel>().ToList();
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
                Tabs.Insert(Tabs.Count - 1, tab);
                _ = tab.ReloadAsync();
            }
            foreach (var gone in current)
            {
                if (SelectedTab == gone)
                    SelectedTab = Home;
                Tabs.Remove(gone);
            }
            Home.Devices.Clear();
            foreach (var tab in Tabs.OfType<DeviceViewModel>())
                Home.Devices.Add(tab);
            OpenRequested();
        });
    }

    /// <summary>For screenshots and checks: VIPER_SYNAPSE_OPEN=02eb opens a device tab, 02eb:5 also opens key 5's
    /// flyout, macros opens the MACROS tab. Applied once, when the devices first arrive.</summary>
    void OpenRequested()
    {
        if (_opened || Environment.GetEnvironmentVariable("VIPER_SYNAPSE_OPEN") is not { Length: > 0 } open)
            return;
        _opened = true;
        if (open == "macros")
        {
            SelectedTab = Macros;
            return;
        }
        var parts = open.Split(':');
        if (Tabs.OfType<DeviceViewModel>().FirstOrDefault(d => d.Key == parts[0]) is not { } device)
            return;
        SelectedTab = device;
        if (parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var number))
            Dispatcher.UIThread.Post(() => device.SelectedKey = device.Rows.FirstOrDefault(r => r.Key.Number == number), DispatcherPriority.Background);
    }

    bool _opened;

    public void Show(TabViewModel tab) => SelectedTab = tab;

    /// <summary>Reports a failed service call where the user can see it.</summary>
    public void Fail(string what, Exception e) => Dispatcher.UIThread.Post(() => Problem = $"{what}: {e.Message}");
}

/// <summary>The SYNAPSE tab: the dashboard of connected devices.</summary>
public sealed partial class HomeViewModel(MainViewModel main) : TabViewModel
{
    public override string Header => "SYNAPSE";

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public void Open(DeviceViewModel device) => main.Show(device);
}
