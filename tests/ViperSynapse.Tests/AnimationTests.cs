using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Tests;

/// <summary>The effects the service draws, each against what it is meant to look like.</summary>
public class AnimationTests
{
    static readonly Rgb Red = new(255, 0, 0), Blue = new(0, 0, 255);
    static readonly IReadOnlyList<LedPoint> Keypad = EffectSupport.Grid(5, 6), Mouse = EffectSupport.Line(3), One = EffectSupport.Line(1);

    static Animation Make(EffectKind kind, IReadOnlyList<LedPoint> leds, ColorMode mode = ColorMode.Single, Func<double?>? temperature = null) =>
        new(new ZoneEffect(kind, Red, Blue, mode), leds, new Random(7), temperature);

    static void Run(Animation a, double seconds)
    {
        for (double t = 0; t < seconds; t += 1 / 30.0)
            a.Step(1 / 30.0);
    }

    static int Brightness(Rgb c) => c.R + c.G + c.B;

    static double RowLevel(Animation a, int row) => Enumerable.Range(0, 6).Average(c => Brightness(a.Colors[row * 6 + c]));

    public static TheoryData<EffectKind, ColorMode> EveryDrawnEffect()
    {
        var data = new TheoryData<EffectKind, ColorMode>();
        foreach (var kind in Enum.GetValues<EffectKind>().Where(k => k is not (EffectKind.Custom or EffectKind.Off)))
            foreach (var mode in EffectSupport.SoftwareModes(kind))
                data.Add(kind, mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryDrawnEffect))]
    public void Every_effect_runs_on_a_keypad_grid_a_mouse_and_a_single_led(EffectKind kind, ColorMode mode)
    {
        foreach (var leds in new[] { Keypad, Mouse, One })
        {
            var a = Make(kind, leds, mode, () => 70);
            bool lit = false;
            for (int frame = 0; frame < 300; frame++)
            {
                if (frame % 10 == 0)
                    a.Press(frame / 10 % leds.Count);
                a.Step(1 / 30.0);
                Assert.Equal(leds.Count, a.Colors.Count);
                lit |= a.Colors.Any(c => Brightness(c) > 0);
            }
            Assert.True(lit, $"{kind} {mode} on {leds.Count} LEDs never lit anything");
        }
    }

    [Theory]
    [InlineData(EffectKind.Strobing, 2)]     // on and off
    [InlineData(EffectKind.Marquee, 3)]      // every third LED lit, three ways round
    [InlineData(EffectKind.Heartbeat, 3)]
    [InlineData(EffectKind.Spectrum)]
    [InlineData(EffectKind.Wave)]
    [InlineData(EffectKind.Breathing)]
    [InlineData(EffectKind.ColorShift)]
    [InlineData(EffectKind.ColorPulse)]
    [InlineData(EffectKind.ColorWave)]
    [InlineData(EffectKind.Visor)]
    [InlineData(EffectKind.Rain)]
    [InlineData(EffectKind.Watercolor)]
    [InlineData(EffectKind.Sequential)]
    [InlineData(EffectKind.Fire)]
    [InlineData(EffectKind.Starlight)]
    [InlineData(EffectKind.Wheel)]
    public void Moving_effects_move(EffectKind kind, int looks = 5)
    {
        var a = Make(kind, Keypad);
        var seen = new HashSet<string>();
        for (int i = 0; i < 20; i++)
        {
            Run(a, 0.25);
            seen.Add(string.Join(",", a.Colors));
        }
        Assert.True(seen.Count >= looks, $"{kind} showed only {seen.Count} different frames in 5 s");
    }

    [Fact]
    public void Static_and_gradient_hold_their_colours()
    {
        var still = Make(EffectKind.Static, Keypad);
        Run(still, 2);
        Assert.All(still.Colors, c => Assert.Equal(Red, c));
        var gradient = Make(EffectKind.Gradient, Keypad);
        Run(gradient, 2);
        Assert.Equal(Red, gradient.Colors[0]);
        Assert.Equal(Blue, gradient.Colors[5]);
        Assert.Equal(gradient.Colors[0], gradient.Colors[6]);
    }

    [Fact]
    public void Temperature_runs_from_the_cool_colour_at_40_to_the_hot_one_at_90()
    {
        double celsius = 30;
        var a = Make(EffectKind.Temperature, Mouse, ColorMode.Dual, () => celsius);
        Assert.Equal(Red, a.Colors[0]);
        celsius = 95;
        a.Step(0.1);
        Assert.Equal(Blue, a.Colors[0]);
        celsius = 65;
        a.Step(0.1);
        Assert.Equal(new Rgb(128, 0, 128), a.Colors[0]);
        var unknown = Make(EffectKind.Temperature, Mouse, ColorMode.Dual, () => null);
        Assert.Equal(Red, unknown.Colors[0]);
    }

    [Fact]
    public void Color_shift_glides_from_the_first_colour_to_the_second_in_three_seconds()
    {
        var a = Make(EffectKind.ColorShift, One, ColorMode.Dual);
        Assert.Equal(Red, a.Colors[0]);
        a.Step(1.5);
        Assert.InRange(a.Colors[0].R, 126, 129);
        Assert.InRange(a.Colors[0].B, 126, 129);
        a.Step(1.5);
        Assert.Equal(Blue, a.Colors[0]);
    }

    [Fact]
    public void Reactive_lights_the_pressed_key_and_fades_it_out()
    {
        var a = Make(EffectKind.Reactive, Keypad);
        Assert.All(a.Colors, c => Assert.Equal(default, c));
        a.Press(8);
        a.Step(1 / 30.0);
        Assert.True(a.Colors[8].R > 200);
        Assert.Equal(default, a.Colors[9]);
        Run(a, 1.6);
        Assert.Equal(default, a.Colors[8]);
    }

    [Fact]
    public void A_ripple_spreads_from_the_pressed_key_and_dies_away()
    {
        var a = Make(EffectKind.Ripple, Keypad);
        a.Press(0);   // top left
        Run(a, 0.15);
        Assert.True(Brightness(a.Colors[1]) > 0, "the ring reaches the next key");
        Assert.Equal(0, Brightness(a.Colors[29]));   // not yet the far corner
        Run(a, 0.8);
        Assert.True(Brightness(a.Colors[29]) > 0 || Brightness(a.Colors[28]) > 0, "the ring reaches the far side");
        Run(a, 1.5);
        Assert.All(a.Colors, c => Assert.Equal(0, Brightness(c)));
    }

    [Fact]
    public void A_mouse_button_lights_the_whole_mouse_for_reactive()
    {
        var a = Make(EffectKind.Reactive, Mouse);
        a.PressAll();
        a.Step(1 / 30.0);
        Assert.All(a.Colors, c => Assert.True(c.R > 200));
    }

    [Fact]
    public void Fire_burns_hottest_at_the_bottom_in_flame_colours()
    {
        var a = Make(EffectKind.Fire, Keypad);
        double bottom = 0, top = 0;
        for (int i = 0; i < 60; i++)
        {
            Run(a, 0.1);
            bottom += RowLevel(a, 4);
            top += RowLevel(a, 0);
            Assert.All(a.Colors, c => Assert.True(c.R >= c.G && c.G >= c.B, $"{c} is not a flame colour"));
        }
        Assert.True(bottom > top * 1.5, $"bottom {bottom:F0} vs top {top:F0}");
        Assert.True(top > 0, "flames reach the top row now and then");
    }

    [Fact]
    public void Rain_falls_from_the_top_row_down()
    {
        var a = Make(EffectKind.Rain, Keypad);
        var firstLit = new int[5];
        Array.Fill(firstLit, -1);
        for (int frame = 0; frame < 90; frame++)
        {
            a.Step(1 / 30.0);
            for (int row = 0; row < 5; row++)
                if (firstLit[row] < 0 && RowLevel(a, row) > 0)
                    firstLit[row] = frame;
        }
        Assert.All(firstLit, f => Assert.True(f >= 0, "every row gets rain"));
        for (int row = 1; row < 5; row++)
            Assert.True(firstLit[row] > firstLit[row - 1], $"row {row} lit at frame {firstLit[row]}, before the row above ({firstLit[row - 1]})");
    }

    [Fact]
    public void Visor_sweeps_across_and_back()
    {
        var a = Make(EffectKind.Visor, EffectSupport.Line(10));
        int Brightest() => Enumerable.Range(0, 10).MaxBy(i => Brightness(a.Colors[i]));
        Assert.Equal(0, Brightest());
        Run(a, 1.25);
        Assert.Equal(9, Brightest());
        Run(a, 1.25);
        Assert.Equal(0, Brightest());
    }

    [Theory]
    [InlineData(new[] { "static", "spectrum", "wave", "breath_single", "breath_dual", "breath_random" }, EffectKind.Wave, ColorMode.Single, true)]
    [InlineData(new[] { "static", "spectrum", "breath_single" }, EffectKind.Wave, ColorMode.Single, false)]       // the Naga: no firmware wave
    [InlineData(new[] { "static", "breath_single" }, EffectKind.Breathing, ColorMode.Random, false)]              // random breathing drawn
    [InlineData(new[] { "static", "starlight_single", "starlight_random" }, EffectKind.Starlight, ColorMode.Dual, false)]
    [InlineData(new[] { "static", "spectrum", "wave" }, EffectKind.Fire, ColorMode.Single, false)]
    [InlineData(new string[0], EffectKind.Custom, ColorMode.Single, true)]
    public void The_firmware_runs_what_the_catalogue_lists_and_the_service_draws_the_rest(string[] zone, EffectKind kind, ColorMode mode, bool onDevice) =>
        Assert.Equal(onDevice, EffectSupport.RunsOnDevice(new ZoneEffect(kind, Red, Blue, mode), zone));

    [Fact]
    public void Hues_go_round_the_wheel()
    {
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHue(0));
        Assert.Equal(new Rgb(0, 255, 0), Rgb.FromHue(1 / 3.0));
        Assert.Equal(new Rgb(0, 0, 255), Rgb.FromHue(2 / 3.0));
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHue(1));
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHue(-1));
    }
}
