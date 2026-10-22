using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Tests;

public class GamepadTests
{
    const ushort EvKey = 1, EvAbs = 3;

    [Fact]
    public void Stick_keys_add_up_and_opposite_keys_cancel()
    {
        var pad = new GamepadState();
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsY, -32767)], pad.Set(GamepadControl.LeftStickUp, true));
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsX, 32767)], pad.Set(GamepadControl.LeftStickRight, true));
        // Down with up held: the stick goes back to the middle.
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsY, 0)], pad.Set(GamepadControl.LeftStickDown, true));
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsY, 32767)], pad.Set(GamepadControl.LeftStickUp, false));
    }

    [Fact]
    public void Triggers_go_full_and_buttons_use_xpad_codes()
    {
        var pad = new GamepadState();
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsZ, 255)], pad.Set(GamepadControl.LeftTrigger, true));
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsZ, 0)], pad.Set(GamepadControl.LeftTrigger, false));
        Assert.Equal([new PadEvent(EvKey, 0x130, 1)], pad.Set(GamepadControl.A, true));
        Assert.Equal([new PadEvent(EvAbs, GamepadState.AbsHat0X, -1)], pad.Set(GamepadControl.DpadLeft, true));
    }

    [Fact]
    public void Two_keys_on_one_control_hold_it_until_both_let_go()
    {
        var pad = new GamepadState();
        Assert.Single(pad.Set(GamepadControl.A, true));
        Assert.Empty(pad.Set(GamepadControl.A, true));
        Assert.Empty(pad.Set(GamepadControl.A, false));
        Assert.Equal([new PadEvent(EvKey, 0x130, 0)], pad.Set(GamepadControl.A, false));
        // A release with nothing held changes nothing.
        Assert.Empty(pad.Set(GamepadControl.B, false));
    }

    [Fact]
    public void A_gamepad_binding_presses_and_releases_its_control()
    {
        var rec = new Recorder();
        var engine = new RemapEngine(rec, rec) { Keymap = Keymap.Empty("k").With(17, new GamepadBinding(GamepadControl.LeftStickUp)) };
        engine.Key(17, 1);
        engine.Key(17, 2);
        engine.Key(17, 0);
        Assert.Equal(["pad:LeftStickUp:1", "pad:LeftStickUp:0"], rec.Events);
    }

    sealed class Recorder : IInputSink, IEngineHost
    {
        public List<string> Events { get; } = [];
        public void Key(ushort code, int value) => Events.Add($"{code}:{value}");
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) => Events.Add($"pad:{control}:{(down ? 1 : 0)}");
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) => throw error;
    }
}
