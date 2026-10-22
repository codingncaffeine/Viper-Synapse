using Avalonia;
using ViperSynapse.App.Services;

namespace ViperSynapse.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"Viper Synapse {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
            return 0;
        }
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("""
                Usage: viper-synapse [--background]

                  --background   Start in the tray without opening the window (how sign-in starts it).
                  --version      Print the version and exit.
                """);
            return 0;
        }

        // A second copy shows the first one's window and leaves.
        var windowBus = new WindowBus();
        if (!windowBus.ClaimAsync().GetAwaiter().GetResult())
            return 0;

        // Read before the UI thread starts: nothing that touches the disk runs on it.
        App.Start = new StartupState(AppPreferences.Load(), Autostart.IsEnabled, args.Contains("--background"), windowBus);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            windowBus.Dispose();
        }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>What Main learned before the UI thread started.</summary>
sealed record StartupState(AppPreferences Preferences, bool StartAtLogin, bool Background, WindowBus Bus);
