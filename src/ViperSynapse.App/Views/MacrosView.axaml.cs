using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class MacrosView : UserControl
{
    public MacrosView()
    {
        InitializeComponent();
        KeystrokeCapture.PointerPressed += (_, _) => KeystrokeCapture.Focus();
        KeystrokeCapture.AddHandler(KeyDownEvent, (_, e) =>
        {
            var code = PhysicalKeys.ToLinux(e.PhysicalKey);
            if (code != 0 && DataContext is MacrosViewModel macros)
            {
                macros.InsertKeystroke(code);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MacrosViewModel macros)
                macros.RecordOptionsRequested += () => RecordOptions.Flyout?.ShowAt(RecordOptions);
        };
        AssignTile.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(AssignTile).Properties.IsLeftButtonPressed && DataContext is MacrosViewModel macros)
                macros.StartAssigningCommand.Execute(null);
        };
    }

    void CancelAssign(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosViewModel macros)
            macros.Assigning = false;
    }
}
