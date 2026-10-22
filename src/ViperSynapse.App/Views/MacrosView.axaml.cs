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
                macros.AddKeystroke(code);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
