using ViperSynapse.Core.Bindings;

namespace ViperSynapse.Tests;

public class SettingsTests
{
    [Fact]
    public void Settings_with_every_kind_of_binding_survive_a_round_trip()
    {
        var macro = Macro.Create("combo", [new KeyStep(30, true), new DelayStep(50), new KeyStep(30, false), new ScrollStep(-2), new TextStep("gg"), new RunStep("kcalc")]);
        var keymap = Keymap.Empty("Keymap 1")
            .With(1, new KeyboardBinding(46, Modifiers.Ctrl, Turbo: true, TurboRate: 15))
            .With(2, new MouseBinding(MouseFunction.Back))
            .With(3, new MacroBinding(macro.Id, MacroPlayback.Times, 4))
            .With(4, new HypershiftBinding())
            .With(5, new SwitchProfileBinding(CycleDirection.Specific, Guid.NewGuid()))
            .With(6, new SwitchKeymapBinding(CycleDirection.Next))
            .With(7, new LaunchBinding("firefox --new-window"))
            .With(8, new TextBinding("hello"))
            .With(9, new DisableBinding())
            .With(1, new KeyboardBinding(2), hypershift: true);
        var profile = Profile.Create("Game") with { Keymaps = [keymap, Keymap.Empty("Keymap 2")], LinkedPrograms = ["game.exe"] };
        var settings = new DeviceSettings([profile], profile.Id, 1);

        var back = SettingsJson.ReadSettings(SettingsJson.Write(settings));

        Assert.Equal(settings.ActiveProfile, back.ActiveProfile);
        Assert.Equal(1, back.ActiveKeymap);
        var k = back.Profiles[0].Keymaps[0];
        Assert.Equal(keymap.Standard.OrderBy(p => p.Key), k.Standard.OrderBy(p => p.Key));
        Assert.Equal(keymap.Hypershift, k.Hypershift);
        Assert.Equal(["game.exe"], back.Profiles[0].LinkedPrograms);

        var macros = SettingsJson.ReadMacros(SettingsJson.Write(new MacroLibrary([macro])));
        Assert.Equal(macro.Steps, macros.Macros[0].Steps);
    }

    [Fact]
    public void Switching_profiles_and_keymaps_wraps_around()
    {
        var a = Profile.Create("A") with { Keymaps = [Keymap.Empty("1"), Keymap.Empty("2"), Keymap.Empty("3")] };
        var b = Profile.Create("B");
        var s = new DeviceSettings([a, b], a.Id, 0);

        Assert.Equal(b.Id, s.SwitchProfile(CycleDirection.Next, null).ActiveProfile);
        Assert.Equal(b.Id, s.SwitchProfile(CycleDirection.Previous, null).ActiveProfile);
        Assert.Equal(2, s.SwitchKeymap(CycleDirection.Previous, 0).ActiveKeymap);
        Assert.Equal(1, s.SwitchKeymap(CycleDirection.Specific, 1).ActiveKeymap);
        Assert.Equal("2", s.SwitchKeymap(CycleDirection.Next, 0).ActiveKeymapOrFirst.Name);
    }
}

public class RecordingTests
{
    static RecordedEvent At(int ms, ushort code, int value) => new(TimeSpan.FromMilliseconds(ms), code, value);

    static readonly RecordedEvent[] Typed =
    [
        At(0, 30, 1), At(80, 30, 2), At(120, 30, 0),
        At(300, 48, 1), At(350, 48, 0),
        new(TimeSpan.FromMilliseconds(400), 0, -1, IsWheel: true),
    ];

    [Fact]
    public void Recorded_delays_keep_the_gaps_and_drop_repeats()
    {
        Assert.Equal<MacroStep>(
            [new KeyStep(30, true), new DelayStep(120), new KeyStep(30, false), new DelayStep(180), new KeyStep(48, true),
             new DelayStep(50), new KeyStep(48, false), new DelayStep(50), new ScrollStep(-1)],
            MacroRecording.ToSteps(Typed, RecordDelayMode.Recorded));
    }

    [Fact]
    public void Default_delay_puts_the_same_gap_everywhere_and_no_delay_puts_none()
    {
        var fixedGaps = MacroRecording.ToSteps(Typed, RecordDelayMode.Default, 25);
        Assert.All(fixedGaps.OfType<DelayStep>(), d => Assert.Equal(25, d.Milliseconds));
        Assert.Equal(4, fixedGaps.OfType<DelayStep>().Count());
        Assert.Empty(MacroRecording.ToSteps(Typed, RecordDelayMode.None).OfType<DelayStep>());
    }

    [Fact]
    public void A_key_already_down_when_recording_starts_is_not_released_by_the_macro()
    {
        var steps = MacroRecording.ToSteps([At(0, 42, 0), At(10, 30, 1), At(20, 30, 0)], RecordDelayMode.None);
        Assert.Equal<MacroStep>([new KeyStep(30, true), new KeyStep(30, false)], steps);
    }
}
