using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Tests;

public class StickTests
{
    const ushort Up = SourceKeys.StickUp, Down = SourceKeys.StickDown, Left = SourceKeys.StickLeft, Right = SourceKeys.StickRight;

    [Fact]
    public void Four_way_presses_only_the_stronger_axis()
    {
        var stick = new StickDirections(ThumbstickMode.FourWay);
        Assert.Equal([(Right, true)], stick.Move(0.9, -0.6));
        // Swinging to straight up lets right go and presses up.
        Assert.Equal([(Right, false), (Up, true)], stick.Move(0.1, -0.9));
        Assert.Equal([(Up, false)], stick.Move(0, 0));
    }

    [Fact]
    public void Eight_way_presses_both_directions_on_a_diagonal()
    {
        var stick = new StickDirections(ThumbstickMode.EightWay);
        Assert.Equal([(Left, true), (Down, true)], stick.Move(-0.8, 0.8));
        Assert.Equal([(Down, false)], stick.Move(-0.8, 0.1));
        Assert.Equal(new HashSet<ushort> { Left }, stick.Down.ToHashSet());
    }

    [Fact]
    public void A_direction_does_not_chatter_at_the_threshold()
    {
        var stick = new StickDirections(ThumbstickMode.EightWay);
        Assert.Equal([(Right, true)], stick.Move(0.55, 0));
        Assert.Empty(stick.Move(0.45, 0));    // between release and press: stays down
        Assert.Empty(stick.Move(0.52, 0));
        Assert.Equal([(Right, false)], stick.Move(0.3, 0));
        Assert.Empty(stick.Move(0.45, 0));    // not pressed again until past the press point
    }

    [Fact]
    public void Analog_mode_presses_nothing()
    {
        Assert.Empty(new StickDirections(ThumbstickMode.Analog).Move(1, 1));
    }

    [Fact]
    public void Unbound_stick_directions_use_the_device_default_and_the_hypershift_button_switches_layers()
    {
        var keymap = Keymap.Empty("k").With(30, new KeyboardBinding(48), hypershift: true);
        var rec = new List<string>();
        var sink = new Sink(rec);
        var engine = new RemapEngine(sink, sink)
        {
            Keymap = keymap,
            Defaults = s => s == Up ? new KeyboardBinding(17) : null,   // W
        };
        engine.Key(Up, 1); engine.Key(Up, 0);
        engine.Key(Down, 1); engine.Key(Down, 0);        // no default: does nothing
        engine.HypershiftButton(true);
        engine.Key(30, 1); engine.Key(30, 0);
        engine.HypershiftButton(false);
        engine.Key(30, 1); engine.Key(30, 0);
        Assert.Equal(["17:1", "17:0", "48:1", "48:0", "30:1", "30:0"], rec);
    }

    sealed class Sink(List<string> events) : IInputSink, IEngineHost
    {
        public void Key(ushort code, int value) => events.Add($"{code}:{value}");
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Gamepad(GamepadControl control, bool down) { }
        public void Report(Exception error) => throw error;
    }
}
