using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;

namespace ViperSynapse.Tests;

/// <summary>The window's own App under Avalonia's headless platform: no display, no bus.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    public static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
}

/// <summary>The LIGHTING page as the user works it: the real DeviceView, an effect picked in its quick effects box.</summary>
public class LightingPageTests
{
    sealed class Sink : ILogSink
    {
        public readonly List<string> Lines = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Lines.Add($"{area}: {messageTemplate}");
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Lines.Add($"{area}: {messageTemplate} {string.Join(" | ", propertyValues)}");
    }

    static void Settle()
    {
        // The page saves 300 ms after the last edit: let that timer run.
        var until = DateTime.UtcNow.AddMilliseconds(450);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData("02EB")]   // Tartarus V2 Pro: one zone and the per-key frame
    [InlineData("008D")]   // Naga Left-Handed 2020: three zones
    [InlineData("00E3")]   // Basilisk V4 Pro: three zones
    public Task Every_quick_effect_can_be_picked_and_stays_picked(string productId) => HeadlessApp.Session.Dispatch(() =>
    {
        var sink = new Sink();
        Logger.Sink = sink;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("test-" + productId, productId, "Test", "keypad", "usb-viper-test", true, true, null, null, null, [], false, null);
        var device = new DeviceViewModel(info, main) { SelectedPage = "LIGHTING" };
        device.Lighting.Load(null);
        var window = new Window { Width = 1440, Height = 1400, Content = new DeviceView { DataContext = device } };
        window.Show();
        Settle();

        var box = window.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, device.Lighting.QuickEffects));
        var choices = device.Lighting.QuickEffects.Select(e => e.Label).ToList();
        Assert.True(choices.Count > 1, "the box offers more than one effect");
        // A red message under a box, however briefly it shows.
        var flashed = new List<string>();
        using var watch = DataValidationErrors.ErrorsProperty.Changed.Subscribe(new Watch(e =>
        {
            if (e.NewValue.GetValueOrDefault() is { } list)
                flashed.AddRange(list.Select(x => $"{e.Sender.GetType().Name} {(e.Sender as Control)?.DataContext?.GetType().Name}: {(x as Exception)?.Message ?? x}"));
        }));
        // Every effect, in an order where each pick is a change from the last, then each of its colour modes.
        foreach (var label in choices.AsEnumerable().Reverse().Concat(choices))
        {
            box.SelectedIndex = box.Items.Cast<EffectChoice>().ToList().FindIndex(e => e.Label == label);
            Settle();
            // Settled = saved and loaded back from the saved settings.
            Assert.Equal(label, device.Lighting.SelectedEffect?.Label);
            Assert.Equal(label, (box.SelectedItem as EffectChoice)?.Label);
            if (device.Lighting.ShowModes)
            {
                var modes = window.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, device.Lighting.ColorModes));
                foreach (var mode in device.Lighting.ColorModes.Reverse().ToList())
                {
                    modes.SelectedItem = mode;
                    Settle();
                    Assert.Equal(mode, device.Lighting.SelectedColorMode);
                    Assert.Equal(label, device.Lighting.SelectedEffect?.Label);
                }
            }
        }
        Assert.True(flashed.Count == 0, string.Join("\n", flashed.Distinct()));
        window.Close();
        Logger.Sink = null;
        Assert.DoesNotContain(sink.Lines, l => l.StartsWith("Binding", StringComparison.Ordinal));
    }, CancellationToken.None);

    sealed class Watch(Action<AvaloniaPropertyChangedEventArgs<IEnumerable<object>?>> seen) : IObserver<AvaloniaPropertyChangedEventArgs<IEnumerable<object>?>>
    {
        public void OnNext(AvaloniaPropertyChangedEventArgs<IEnumerable<object>?> value) => seen(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
