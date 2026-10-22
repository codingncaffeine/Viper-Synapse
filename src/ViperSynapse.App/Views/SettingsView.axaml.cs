using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ViperSynapse.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void OpenReleases(object? sender, RoutedEventArgs e) => Open("https://github.com/codingncaffeine/Viper-Synapse/releases");

    void OpenProject(object? sender, RoutedEventArgs e) => Open("https://github.com/codingncaffeine/Viper-Synapse");

    void Open(string url)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            _ = top.Launcher.LaunchUriAsync(new Uri(url));
    }
}
