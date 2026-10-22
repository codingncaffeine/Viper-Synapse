using System.Runtime.InteropServices;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Service;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        string root = args.Length >= 2 && args[0] == "--config" ? args[1] : SettingsStore.DefaultRoot();
        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });

        UinputDevice output;
        try
        {
            output = UinputDevice.Create("Viper Synapse virtual input");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot make the virtual keyboard: {e.Message}. Install the Viper Synapse udev rule.");
            return 2;
        }
        using var _ = output;
        using var devices = new DeviceManager(new SettingsStore(root), output);
        using var bus = new DaemonBus(devices, () =>
        {
            // Grabbed devices are heard through the virtual device, which carries what they send.
            var held = devices.Devices.Where(d => d.Remapping).SelectMany(d => d.InputNodes);
            return MacroRecorder.KeyboardsAndMice(held);
        }, stop.Cancel);
        try
        {
            await bus.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or Tmds.DBus.Protocol.DBusConnectionException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        devices.Refresh();
        foreach (var d in devices.Devices)
            Console.WriteLine($"{d.Name} ({d.ProductId}) {d.Firmware} {(d.Remapping ? "remapping" : "idle")} {d.Problem}");

        // Plugging and unplugging shows up as nodes coming and going in /dev/input.
        using var watcher = new FileSystemWatcher("/dev/input") { IncludeSubdirectories = false, EnableRaisingEvents = true };
        var pending = 0;
        void Changed()
        {
            if (Interlocked.Exchange(ref pending, 1) == 1)
                return;
            // udev needs a moment to set the permissions on a new node.
            Task.Delay(500).ContinueWith(_ => { Volatile.Write(ref pending, 0); devices.Refresh(); }, TaskScheduler.Default);
        }
        watcher.Created += (_, _) => Changed();
        watcher.Deleted += (_, _) => Changed();

        try { await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return 0;
    }
}
