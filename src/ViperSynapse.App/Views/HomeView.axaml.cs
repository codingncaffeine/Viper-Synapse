using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    HomeViewModel? Home => DataContext as HomeViewModel;

    void OpenDevice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceViewModel device })
            Home?.Open(device);
    }

    void OpenLinkedGames(object? sender, RoutedEventArgs e) => Home?.OpenLinkedGames();

    void OpenMacros(object? sender, RoutedEventArgs e) => Home?.OpenMacros();

    void OpenSettings(object? sender, RoutedEventArgs e) => Home?.OpenSettings();
}
