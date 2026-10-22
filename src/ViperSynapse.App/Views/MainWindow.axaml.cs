using Avalonia.Controls;

namespace ViperSynapse.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // For screenshots of pages taller than the default window.
        if (int.TryParse(System.Environment.GetEnvironmentVariable("VIPER_SYNAPSE_HEIGHT"), out var height) && height > 0)
            Height = height;
        TopBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (e.ClickCount == 2) ToggleMaximized();
            else BeginMoveDrag(e);
        };
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();
        HelpButton.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/codingncaffeine/Viper-Synapse"));
        Edge(EdgeLeft, WindowEdge.West);
        Edge(EdgeRight, WindowEdge.East);
        Edge(EdgeBottom, WindowEdge.South);
        Edge(EdgeBottomRight, WindowEdge.SouthEast);
        Edge(EdgeBottomLeft, WindowEdge.SouthWest);
    }

    void Edge(Control handle, WindowEdge edge) => handle.PointerPressed += (_, e) =>
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
            BeginResizeDrag(edge, e);
    };

    void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
