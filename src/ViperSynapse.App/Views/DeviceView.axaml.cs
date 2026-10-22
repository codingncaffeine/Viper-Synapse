using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class DeviceView : UserControl
{
    public DeviceView()
    {
        InitializeComponent();
        PaintCanvas.KeyPainted += row =>
        {
            if (DataContext is DeviceViewModel device)
                device.Lighting.PaintKey(row.Key);
        };
    }

    void ShowLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel device)
            device.ShowPhoto = false;
    }

    void ActivateStage(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StageRow row } && DataContext is DeviceViewModel device)
            device.Performance.Activate(row);
    }

    void PaintCell(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LedCell cell } && DataContext is DeviceViewModel device)
            device.Lighting.PaintCell(cell);
    }
}
