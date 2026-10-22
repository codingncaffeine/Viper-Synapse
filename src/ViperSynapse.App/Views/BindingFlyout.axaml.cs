using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class BindingFlyout : UserControl
{
    public BindingFlyout()
    {
        InitializeComponent();
        KeyCapture.PointerPressed += (_, _) => KeyCapture.Focus();
        // Tunnel, so Tab and the arrow keys are caught here instead of moving focus.
        KeyCapture.AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
    }

    void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not BindingEditorViewModel editor)
            return;
        var code = PhysicalKeys.ToLinux(e.PhysicalKey);
        if (code == 0)
            return;
        editor.CapturedKey = code;
        e.Handled = true;
    }

    async void Browse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BindingEditorViewModel editor || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a program", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            editor.Command = path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
