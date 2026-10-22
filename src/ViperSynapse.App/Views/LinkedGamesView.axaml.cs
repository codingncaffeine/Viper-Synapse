using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class LinkedGamesView : UserControl
{
    public LinkedGamesView()
    {
        InitializeComponent();
        AddTile.AddHandler(DragDrop.DropEvent, Dropped);
    }

    LinkedGamesViewModel? Model => DataContext as LinkedGamesViewModel;

    void PickGame(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameRow game } && Model is { } model)
            model.SelectedGame = game;
    }

    async void Browse(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a game or program", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            model.AddProgram(Path.GetFileName(path));
    }

    /// <summary>A game dropped from a file manager (or a .desktop launcher) is linked by its file name.</summary>
    void Dropped(object? sender, DragEventArgs e)
    {
        if (Model is not { } model || e.DataTransfer.TryGetFiles() is not { } files)
            return;
        foreach (var file in files)
            if (file.TryGetLocalPath() is { } path)
                model.AddProgram(Path.GetFileName(path));
    }
}
