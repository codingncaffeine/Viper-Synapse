using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    void OpenDevice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceViewModel device } && DataContext is HomeViewModel home)
            home.Open(device);
    }
}
