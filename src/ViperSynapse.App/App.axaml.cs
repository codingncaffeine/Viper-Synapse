using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;

namespace ViperSynapse.App;

public sealed class App : Application
{
    internal static StartupState? Start { get; set; }

    IClassicDesktopStyleApplicationLifetime? _desktop;
    DaemonClient? _client;
    MainViewModel? _main;
    MainWindow? _window;
    bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var start = Start ?? new StartupState(new AppPreferences(), false, false, new WindowBus());
            _desktop = desktop;
            _client = new DaemonClient();
            _main = new MainViewModel(_client, start.Preferences, start.StartAtLogin);
            BuildTray();
            start.Bus.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            if (!start.Background)
                ShowWindow();
            _ = _main.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void BuildTray()
    {
        var open = new NativeMenuItem("Open Viper Synapse");
        open.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://viper-synapse/Assets/icon.png"))),
            ToolTipText = "Viper Synapse",
            Menu = [open, new NativeMenuItemSeparator(), quit],
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
    }

    void ShowWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow { DataContext = _main };
            _window.Closing += (_, e) =>
            {
                if (_quitting)
                    return;
                if (_main!.Settings.CloseToTray)
                {
                    // Closing hides: bindings and macros keep running, the tray brings the window back.
                    e.Cancel = true;
                    _window.Hide();
                }
                else
                {
                    e.Cancel = true;
                    Quit();
                }
            };
        }
        _window.Show();
        _window.WindowState = _window.WindowState == WindowState.Minimized ? WindowState.Normal : _window.WindowState;
        _window.Activate();
    }

    /// <summary>Quits the app and its service, as Synapse's Exit does: software bindings and macros stop with it.</summary>
    void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        _ = Task.Run(async () =>
        {
            try { await _client!.QuitServiceAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or Tmds.DBus.Protocol.DBusErrorReplyException or Tmds.DBus.Protocol.DBusConnectionException) { }
            await _client!.DisposeAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => _desktop?.Shutdown());
        });
    }
}
