using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

/// <summary>
/// The service's device manager against a stand-in device: a uinput keyboard claiming to be a Tartarus Pro
/// (a model not plugged in here, so no real device is grabbed) on a made-up USB port. Settings live in a
/// throwaway folder; the output device is grabbed by the test so nothing reaches the desktop.
/// </summary>
public class ServiceTests : IDisposable
{
    const ushort StandIn = 0x0244, A = 30, C = 46;
    readonly string _config = Path.Combine(Path.GetTempPath(), "viper-synapse-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config))
            Directory.Delete(_config, recursive: true);
    }

    static bool UinputAvailable()
    {
        try { UinputDevice.Create("viper-synapse-probe").Dispose(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string WaitForNode(UinputDevice device)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (device.EventNode is { } node && File.Exists(node))
            {
                try { EvdevDevice.Open(node).Dispose(); return node; }
                catch (UnauthorizedAccessException) { }
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException($"{device.Name}: no readable event node appeared");
    }

    static List<string> ReadKeys(EvdevDevice output, int count)
    {
        var keys = new List<string>();
        var events = new InputEvent[64];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (keys.Count < count && DateTime.UtcNow < deadline)
        {
            int n = output.Read(events, TimeSpan.FromMilliseconds(100));
            for (int i = 0; i < n; i++)
                if (events[i].Type == EventTypes.Key)
                    keys.Add($"{KeyCodes.Name(events[i].Code)}:{events[i].Value}");
        }
        return keys;
    }

    [Fact]
    public void A_linked_program_brings_its_profile_forward_and_leaving_it_brings_the_chosen_one_back()
    {
        Assert.SkipUnless(UinputAvailable(), "/dev/uinput is not available");
        using var standIn = UinputDevice.Create("Razer Tartarus Pro (test stand-in)", 0x1532, StandIn, phys: "usb-viper-test-2/input1");
        using var output = UinputDevice.Create("viper-synapse-test-output");
        WaitForNode(standIn);
        using var manager = new DeviceManager(new SettingsStore(_config), output);
        manager.Refresh();

        var everyday = Profile.Create("Everyday");
        var game = Profile.Create("Game") with { LinkedPrograms = ["SomeGame.exe"] };
        manager.SetSettings("0244", new DeviceSettings([everyday, game], everyday.Id, 0));

        manager.Focused(new FocusedProgram("somegame.exe", null));
        Assert.Equal(game.Id, manager.GetSettings("0244").ActiveProfile);
        manager.Focused(new FocusedProgram("firefox", "firefox"));
        Assert.Equal(everyday.Id, manager.GetSettings("0244").ActiveProfile);
        // A window class match works too (Hyprland reports only the class).
        manager.Focused(new FocusedProgram(null, "SomeGame"));
        Assert.Equal(game.Id, manager.GetSettings("0244").ActiveProfile);
    }

    [Theory]
    [InlineData("game.exe", null, "Game.exe", true)]
    [InlineData("game", null, "/home/me/Games/game.exe", true)]
    [InlineData("wine64-preloader", "steam_app_123", "steam_app_123", true)]
    [InlineData("firefox", "firefox", "game.exe", false)]
    [InlineData(null, null, "game", false)]
    public void Linked_entries_match_the_executable_or_the_window_class(string? exe, string? windowClass, string linked, bool matches) =>
        Assert.Equal(matches, new FocusedProgram(exe, windowClass).Matches(linked));

    [Fact]
    public void A_binding_saved_in_settings_takes_over_the_device_and_letting_it_go_releases_the_device()
    {
        Assert.SkipUnless(UinputAvailable(), "/dev/uinput is not available");

        using var standIn = UinputDevice.Create("Razer Tartarus Pro (test stand-in)", 0x1532, StandIn, phys: "usb-viper-test-1/input1");
        using var output = UinputDevice.Create("viper-synapse-test-output");
        using var outputNode = EvdevDevice.Open(WaitForNode(output));
        outputNode.Grab();
        var standInNode = WaitForNode(standIn);

        using var manager = new DeviceManager(new SettingsStore(_config), output);
        manager.Refresh();
        var device = manager.Devices.Single(d => d.ProductId == "0244");
        Assert.Equal("Razer Tartarus Pro", device.Name);
        Assert.False(device.Remapping);   // nothing bound: left alone

        var settings = manager.GetSettings("0244");
        var bound = settings with
        {
            Profiles = [settings.Active with { Keymaps = [Keymap.Empty("Keymap 1").With(A, new KeyboardBinding(C))] }],
        };
        manager.SetSettings("0244", bound);
        Assert.True(manager.Devices.Single(d => d.ProductId == "0244").Remapping);

        standIn.Key(A, 1); standIn.Sync();
        standIn.Key(A, 0); standIn.Sync();
        Assert.Equal(["KEY_C:1", "KEY_C:0"], ReadKeys(outputNode, 2));

        // A second service start reads the same settings back from disk.
        Assert.Equal(bound.ActiveProfile, new SettingsStore(_config).LoadDevice("0244").ActiveProfile);

        manager.SetSettings("0244", settings);
        Assert.False(manager.Devices.Single(d => d.ProductId == "0244").Remapping);
        using var free = EvdevDevice.Open(standInNode);
        free.Grab();   // nobody holds it any more
    }
}
