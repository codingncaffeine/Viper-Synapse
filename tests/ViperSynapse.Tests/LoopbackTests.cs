using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

/// <summary>
/// The whole input path through the kernel: a stand-in device made with uinput, a session that grabs it
/// and remaps it, and the virtual output device, which the test grabs itself so that no key ever reaches
/// the desktop. Skipped where /dev/uinput cannot be opened (CI, a machine without the udev rule).
/// </summary>
public class LoopbackTests
{
    const ushort A = 30, B = 48, C = 46;

    sealed class NoHost : IEngineHost
    {
        public void Launch(string command) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Report(Exception error) { }
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
                catch (UnauthorizedAccessException) { }   // udev has not applied the ACL yet
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
    public void A_remapped_key_comes_out_of_the_virtual_device()
    {
        Assert.SkipUnless(UinputAvailable(), "/dev/uinput is not available");

        using var source = UinputDevice.Create("viper-synapse-test-source");
        using var output = UinputDevice.Create("viper-synapse-test-output");
        using var outputNode = EvdevDevice.Open(WaitForNode(output));
        outputNode.Grab();
        var sourceNode = WaitForNode(source);

        var engine = new RemapEngine(output, new NoHost())
        {
            Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Shift)),
        };
        using (var session = DeviceSession.Start([sourceNode], engine))
        {
            Assert.Equal([sourceNode], session.NodePaths);
            source.Key(A, 1); source.Sync();
            source.Key(A, 0); source.Sync();
            source.Key(B, 1); source.Sync();
            source.Key(B, 0); source.Sync();

            Assert.Equal(["KEY_LEFTSHIFT:1", "KEY_C:1", "KEY_C:0", "KEY_LEFTSHIFT:0", "KEY_B:1", "KEY_B:0"], ReadKeys(outputNode, 6));
        }
    }

    [Fact]
    public void The_virtual_device_is_a_keyboard_and_mouse_and_never_a_game_controller()
    {
        Assert.SkipUnless(UinputAvailable(), "/dev/uinput is not available");
        using var output = UinputDevice.Create("viper-synapse-test-output");
        using var node = EvdevDevice.Open(WaitForNode(output));
        var keys = node.Capabilities(EventTypes.Key);
        Assert.Contains((ushort)30, keys);                       // KEY_A
        Assert.Contains(KeyCodes.BtnLeft, keys);
        Assert.Contains((ushort)0x73, keys);                     // KEY_VOLUMEUP
        Assert.DoesNotContain(keys, KeyCodes.IsJoystickButton);  // no BTN_SOUTH, BTN_TRIGGER, ...
        Assert.Empty(node.Capabilities(EventTypes.Abs));
    }

    [Theory]
    [InlineData(0x130, true)]   // BTN_SOUTH
    [InlineData(0x120, true)]   // BTN_TRIGGER
    [InlineData(0x2C0, true)]   // BTN_TRIGGER_HAPPY1
    [InlineData(0x110, false)]  // BTN_LEFT
    [InlineData(30, false)]     // KEY_A
    public void Controller_buttons_are_told_apart(int code, bool joystick) =>
        Assert.Equal(joystick, KeyCodes.IsJoystickButton((ushort)code));

    [Fact]
    public void In_four_way_mode_the_keypad_stick_types_its_directions_and_analog_mode_leaves_it_alone()
    {
        Assert.SkipUnless(UinputAvailable(), "/dev/uinput is not available");
        using var pad = UinputDevice.CreateTestGamepad("Tartarus stick (test stand-in)", 0x1532, 0x0244, "usb-viper-test-4/input0");
        using var output = UinputDevice.Create("viper-synapse-test-output");
        using var outputNode = EvdevDevice.Open(WaitForNode(output));
        outputNode.Grab();
        var padNode = WaitForNode(pad);

        // Analog: the gamepad node is not taken.
        using (var analog = DeviceSession.Start([padNode], new RemapEngine(output, new NoHost()), ThumbstickMode.Analog))
            Assert.Empty(analog.NodePaths);

        var engine = new RemapEngine(output, new NoHost())
        {
            Defaults = s => s switch
            {
                SourceKeys.StickUp => new KeyboardBinding(17),     // W
                SourceKeys.StickRight => new KeyboardBinding(32),  // D
                _ => null,
            },
            Keymap = Keymap.Empty("k").With(SourceKeys.StickClick, new KeyboardBinding(57)),  // Space
        };
        using var session = DeviceSession.Start([padNode], engine, ThumbstickMode.FourWay);
        Assert.Equal([padNode], session.NodePaths);
        void Stick(int x, int y) { pad.Absolute(3, x); pad.Absolute(4, y); pad.Sync(); }
        Stick(0, -30000);      // up
        Stick(0, 0);
        Stick(30000, 2000);    // right
        Stick(0, 0);
        pad.Key(0x13E, 1); pad.Sync(); pad.Key(0x13E, 0); pad.Sync();
        Assert.Equal(["KEY_W:1", "KEY_W:0", "KEY_D:1", "KEY_D:0", "KEY_SPACE:1", "KEY_SPACE:0"], ReadKeys(outputNode, 6));
    }

    [Fact]
    public void The_keypad_button_reports_are_read_as_recorded_from_the_device()
    {
        var seen = new List<string>();
        var buttons = (KeypadButtons)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(KeypadButtons));
        buttons.Hypershift += held => seen.Add($"hypershift {held}");
        buttons.KeymapSelected += k => seen.Add($"keymap {k}");
        buttons.ProfileSelected += p => seen.Add($"profile {p}");
        buttons.Handle([0x04, 0x01, 0x00]);
        buttons.Handle([0x04, 0x00, 0x00]);
        buttons.Handle([0x05, 0x5E, 0x03]);
        buttons.Handle([0x05, 0x01, 0x04]);
        buttons.Handle([0x0B, 0x00]);
        Assert.Equal(["hypershift True", "hypershift False", "keymap 3", "profile 4"], seen);
    }

    [Fact]
    public void Closing_the_session_lets_go_of_a_held_key_and_of_the_grab()
    {
        Assert.SkipUnless(UinputAvailable(), "/dev/uinput is not available");

        using var source = UinputDevice.Create("viper-synapse-test-source");
        using var output = UinputDevice.Create("viper-synapse-test-output");
        using var outputNode = EvdevDevice.Open(WaitForNode(output));
        outputNode.Grab();
        var sourceNode = WaitForNode(source);

        var engine = new RemapEngine(output, new NoHost()) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C)) };
        var session = DeviceSession.Start([sourceNode], engine);
        source.Key(A, 1); source.Sync();
        Assert.Equal(["KEY_C:1"], ReadKeys(outputNode, 1));
        session.Dispose();
        Assert.Equal(["KEY_C:0"], ReadKeys(outputNode, 1));

        // The grab is gone: someone else can take the source now.
        using var again = EvdevDevice.Open(sourceNode);
        again.Grab();
    }
}
