using Microsoft.Extensions.Time.Testing;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Tests;

public class EngineTests
{
    const ushort A = 30, B = 48, C = 46, Caps = 58, Tab = 15;

    sealed class Recorder : IInputSink, IEngineHost
    {
        readonly List<string> _events = [];
        public readonly Dictionary<Guid, Macro> Macros = [];
        public readonly List<string> Launched = [];

        public void Key(ushort code, int value) { lock (_events) _events.Add($"{KeyCodes.Name(code)}:{value}"); }
        public void Relative(ushort axis, int value) { lock (_events) _events.Add($"REL{axis}:{value}"); }
        public void Sync() { }
        public void Launch(string command) { lock (_events) Launched.Add(command); }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { lock (_events) _events.Add($"profile:{direction}"); }
        public void SwitchKeymap(CycleDirection direction, int keymap) { lock (_events) _events.Add($"keymap:{direction}"); }
        public Macro? FindMacro(Guid id) => Macros.GetValueOrDefault(id);
        public void Report(Exception error) => throw error;

        public string[] Events { get { lock (_events) return [.. _events]; } }
        public int Count(string e) => Events.Count(x => x == e);
        public void Clear() { lock (_events) _events.Clear(); }

        /// <summary>Background turbo and macro tasks continue on the thread pool after the fake clock moves.</summary>
        public void WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Gave up; events: " + string.Join(' ', Events));
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>A fake clock that counts the timers made on it, so a test can wait until a delay is pending
    /// before moving time: moving it earlier would be lost.</summary>
    sealed class CountingTime : FakeTimeProvider
    {
        int _timers;
        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Counted once registered: moving time between the count and the registration would be lost.
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }

        public void AdvanceWhenPending(int timers, TimeSpan by, Recorder rec)
        {
            rec.WaitFor(() => Timers >= timers);
            Advance(by);
        }
    }

    static (RemapEngine, Recorder, CountingTime) Make(Keymap keymap)
    {
        var rec = new Recorder();
        var time = new CountingTime();
        return (new RemapEngine(rec, rec, time) { Keymap = keymap }, rec, time);
    }

    [Fact]
    public void An_unbound_key_passes_through_with_its_repeats()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k"));
        engine.Key(A, 1);
        engine.Key(A, 2);
        engine.Key(A, 0);
        Assert.Equal(["KEY_A:1", "KEY_A:2", "KEY_A:0"], rec.Events);
    }

    [Fact]
    public void A_keyboard_function_holds_its_modifiers_around_the_key()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl | Modifiers.Shift)));
        engine.Key(A, 1);
        engine.Key(A, 2);
        engine.Key(A, 0);
        Assert.Equal(["KEY_LEFTCTRL:1", "KEY_LEFTSHIFT:1", "KEY_C:1", "KEY_C:0", "KEY_LEFTSHIFT:0", "KEY_LEFTCTRL:0"], rec.Events);
    }

    [Fact]
    public void A_disabled_key_sends_nothing()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new DisableBinding()));
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Hypershift_switches_layers_and_a_key_releases_what_it_started()
    {
        var keymap = Keymap.Empty("k")
            .With(Caps, new HypershiftBinding())
            .With(A, new KeyboardBinding(B))
            .With(A, new KeyboardBinding(C), hypershift: true);
        var (engine, rec, _) = Make(keymap);

        engine.Key(A, 1); engine.Key(A, 0);
        engine.Key(Caps, 1);
        engine.Key(A, 1);
        engine.Key(Caps, 0);   // Hypershift let go before the key
        engine.Key(A, 0);
        engine.Key(Tab, 1); engine.Key(Tab, 0);

        Assert.Equal(["KEY_B:1", "KEY_B:0", "KEY_C:1", "KEY_C:0", "KEY_TAB:1", "KEY_TAB:0"], rec.Events);
    }

    [Fact]
    public void A_key_missing_from_hypershift_keeps_its_standard_binding()
    {
        var keymap = Keymap.Empty("k").With(Caps, new HypershiftBinding()).With(A, new KeyboardBinding(B));
        var (engine, rec, _) = Make(keymap);
        engine.Key(Caps, 1);
        engine.Key(A, 1); engine.Key(A, 0);
        Assert.Equal(["KEY_B:1", "KEY_B:0"], rec.Events);
    }

    [Fact]
    public void Turbo_repeats_at_its_rate_and_stops_on_release()
    {
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(B, Turbo: true, TurboRate: 10)));
        engine.Key(A, 1);
        rec.WaitFor(() => rec.Count("KEY_B:1") == 1);
        for (int i = 2; i <= 10; i++)
        {
            time.AdvanceWhenPending(i - 1, TimeSpan.FromMilliseconds(100), rec);
            int expected = i;
            rec.WaitFor(() => rec.Count("KEY_B:1") == expected);
        }
        engine.Key(A, 0);
        time.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(50);
        Assert.Equal(10, rec.Count("KEY_B:1"));
        Assert.Equal(10, rec.Count("KEY_B:0"));
    }

    [Fact]
    public void A_macro_plays_its_steps_with_their_delays()
    {
        var macro = Macro.Create("m", [new KeyStep(B, true), new KeyStep(B, false), new DelayStep(500), new KeyStep(C, true), new KeyStep(C, false)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id)));
        rec.Macros[macro.Id] = macro;

        engine.Key(A, 1);
        engine.Key(A, 0);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(499), rec);
        Thread.Sleep(30);
        Assert.Equal(0, rec.Count("KEY_C:1"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        rec.WaitFor(() => rec.Count("KEY_C:0") == 1);
        Assert.Equal(["KEY_B:1", "KEY_B:0", "KEY_C:1", "KEY_C:0"], rec.Events);
    }

    [Fact]
    public void A_while_held_macro_stops_on_release_and_lets_go_of_its_keys()
    {
        var macro = Macro.Create("hold", [new KeyStep(B, true), new DelayStep(100), new KeyStep(B, false), new DelayStep(100)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.WhileHeld)));
        rec.Macros[macro.Id] = macro;

        engine.Key(A, 1);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(100), rec);
        time.AdvanceWhenPending(2, TimeSpan.FromMilliseconds(100), rec);
        rec.WaitFor(() => rec.Count("KEY_B:1") == 2);   // second pass, B down again
        engine.Key(A, 0);
        rec.WaitFor(() => rec.Count("KEY_B:0") == 2);   // the stuck B is let go
        time.Advance(TimeSpan.FromSeconds(2));
        Thread.Sleep(50);
        Assert.Equal(2, rec.Count("KEY_B:1"));
    }

    [Fact]
    public void A_toggle_macro_starts_on_one_press_and_stops_on_the_next()
    {
        var macro = Macro.Create("toggle", [new KeyStep(B, true), new KeyStep(B, false), new DelayStep(100)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.Toggle)));
        rec.Macros[macro.Id] = macro;

        engine.Key(A, 1); engine.Key(A, 0);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(100), rec);
        rec.WaitFor(() => rec.Count("KEY_B:1") == 2);
        rec.WaitFor(() => time.Timers >= 2);
        engine.Key(A, 1); engine.Key(A, 0);
        Thread.Sleep(30);
        time.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(50);
        Assert.Equal(2, rec.Count("KEY_B:1"));
    }

    [Fact]
    public void A_macro_played_a_set_number_of_times_ends_by_itself()
    {
        var macro = Macro.Create("three", [new KeyStep(B, true), new KeyStep(B, false)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.Times, 3)));
        rec.Macros[macro.Id] = macro;
        engine.Key(A, 1); engine.Key(A, 0);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(1), rec);
        time.AdvanceWhenPending(2, TimeSpan.FromMilliseconds(1), rec);
        rec.WaitFor(() => rec.Count("KEY_B:0") == 3);
        Thread.Sleep(30);
        time.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(30);
        Assert.Equal(3, rec.Count("KEY_B:1"));
    }

    [Fact]
    public void A_bound_wheel_fires_once_per_detent()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(SourceKeys.WheelUp, new KeyboardBinding(Tab)));
        engine.Wheel(RelativeAxes.Wheel, 1);
        engine.Wheel(RelativeAxes.WheelHiRes, 120);
        engine.Wheel(RelativeAxes.Wheel, -1);
        engine.Wheel(RelativeAxes.WheelHiRes, -120);
        Assert.Equal(["KEY_TAB:1", "KEY_TAB:0", "REL8:-1", "REL11:-120"], rec.Events);
    }

    [Fact]
    public void Switches_and_launches_go_to_the_host()
    {
        var keymap = Keymap.Empty("k")
            .With(A, new SwitchProfileBinding(CycleDirection.Next))
            .With(B, new SwitchKeymapBinding(CycleDirection.Previous))
            .With(C, new LaunchBinding("kcalc"));
        var (engine, rec, _) = Make(keymap);
        foreach (var k in new[] { A, B, C }) { engine.Key(k, 1); engine.Key(k, 0); }
        Assert.Equal(["profile:Next", "keymap:Previous"], rec.Events);
        Assert.Equal(["kcalc"], rec.Launched);
    }

    [Fact]
    public void Reset_lets_go_of_every_held_key()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)));
        engine.Key(A, 1);
        engine.Key(B, 1);
        engine.Reset();
        Assert.Contains("KEY_C:0", rec.Events);
        Assert.Contains("KEY_LEFTCTRL:0", rec.Events);
        Assert.Contains("KEY_B:0", rec.Events);
    }

    [Fact]
    public void Text_is_typed_with_shift_where_a_us_keyboard_needs_it()
    {
        var steps = TextTyper.Steps("Hi!").Select(s => $"{KeyCodes.Name(s.Code)}:{(s.Down ? 1 : 0)}");
        Assert.Equal(["KEY_LEFTSHIFT:1", "KEY_H:1", "KEY_H:0", "KEY_LEFTSHIFT:0", "KEY_I:1", "KEY_I:0",
            "KEY_LEFTSHIFT:1", "KEY_1:1", "KEY_1:0", "KEY_LEFTSHIFT:0"], steps);
    }
}
