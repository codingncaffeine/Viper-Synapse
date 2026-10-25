namespace ViperSynapse.Core.Lighting;

/// <summary>Where an LED sits on the device: X across and Y down, each 0 to 1.</summary>
public readonly record struct LedPoint(double X, double Y);

/// <summary>Which effects the device's firmware runs itself, and which the service draws frame by frame.</summary>
public static class EffectSupport
{
    /// <summary>Effects only the service can draw: the device has no firmware effect for them.</summary>
    public static bool SoftwareOnly(EffectKind kind) => kind >= EffectKind.Fire;

    /// <summary>The catalogue's effect name prefix for a firmware effect ("breath" for Breathing); null for the ones
    /// only the service draws.</summary>
    public static string? CataloguePrefix(EffectKind kind) => kind switch
    {
        EffectKind.Static => "static",
        EffectKind.Breathing => "breath",
        EffectKind.Spectrum => "spectrum",
        EffectKind.Wave => "wave",
        EffectKind.Reactive => "reactive",
        EffectKind.Starlight => "starlight",
        EffectKind.Wheel => "wheel",
        EffectKind.Off => "none",
        _ => null,
    };

    /// <summary>The colour modes a zone's firmware runs an effect in, from the catalogue's names (static, breath_single,
    /// starlight_random, ...); empty when the firmware does not run it.</summary>
    public static IReadOnlyList<ColorMode> NativeModes(EffectKind kind, IEnumerable<string> zoneEffects)
    {
        if (CataloguePrefix(kind) is not { } prefix)
            return [];
        return zoneEffects.Where(n => n == prefix || n.StartsWith(prefix + "_", StringComparison.Ordinal))
            .Select(n => n.EndsWith("_dual", StringComparison.Ordinal) ? ColorMode.Dual
                : n.EndsWith("_random", StringComparison.Ordinal) ? ColorMode.Random : ColorMode.Single)
            .Distinct().Order().ToList();
    }

    /// <summary>The firmware runs this effect in this mode on the zone; otherwise the service draws it.</summary>
    public static bool RunsOnDevice(ZoneEffect effect, IEnumerable<string> zoneEffects) =>
        effect.Kind == EffectKind.Custom || NativeModes(effect.Kind, zoneEffects).Contains(effect.Colors);

    /// <summary>The colour modes an effect offers when the service draws it.</summary>
    public static IReadOnlyList<ColorMode> SoftwareModes(EffectKind kind) => kind switch
    {
        EffectKind.Off or EffectKind.Spectrum or EffectKind.Wave or EffectKind.Wheel or EffectKind.Fire or EffectKind.Watercolor or EffectKind.Custom => [ColorMode.Single],
        EffectKind.Static or EffectKind.Reactive or EffectKind.Ripple or EffectKind.Strobing or EffectKind.Heartbeat or EffectKind.Visor or EffectKind.Rain or EffectKind.Sequential => [ColorMode.Single, ColorMode.Random],
        EffectKind.Gradient or EffectKind.ColorShift or EffectKind.ColorWave or EffectKind.Marquee or EffectKind.Temperature => [ColorMode.Dual],
        _ => [ColorMode.Single, ColorMode.Dual, ColorMode.Random],
    };

    /// <summary>Lays a zone device's LEDs on a line in the order given.</summary>
    public static IReadOnlyList<LedPoint> Line(int count) =>
        Enumerable.Range(0, count).Select(i => new LedPoint(count == 1 ? 0.5 : (double)i / (count - 1), 0.5)).ToList();

    /// <summary>A rows x columns grid, row by row.</summary>
    public static IReadOnlyList<LedPoint> Grid(int rows, int columns) =>
        Enumerable.Range(0, rows * columns).Select(i => new LedPoint(
            columns == 1 ? 0.5 : (double)(i % columns) / (columns - 1),
            rows == 1 ? 0.5 : (double)(i / columns) / (rows - 1))).ToList();
}

/// <summary>
/// One effect drawn by the service: the colour of every LED, advanced a step at a time. The effects follow Razer
/// Synapse's (Fire, Ripple, Reactive, Starlight, Wave, Wheel, ...) and Corsair iCUE's (Color Shift, Color Pulse, Color
/// Wave, Visor, Rain, Watercolor, Marquee, Sequential, Strobing, Gradient, Heartbeat, Temperature), drawn from the
/// effect's colours, colour mode, speed and direction. Not thread safe: one caller steps it.
/// </summary>
public sealed class Animation
{
    readonly ZoneEffect _effect;
    readonly IReadOnlyList<LedPoint> _leds;
    readonly Random _random;
    readonly Func<double?> _temperature;
    readonly Rgb[] _colors;
    readonly double[] _level;      // per LED: brightness of a fading light (Reactive, Starlight, Rain, Fire's heat)
    readonly Rgb[] _tint;          // per LED: the colour that light has
    readonly List<(LedPoint At, double Born, Rgb Color)> _ripples = [];
    double _time;
    int _cycle = -1, _tick;
    Rgb _cycleA, _cycleB;

    /// <param name="effect">The effect and its options.</param>
    /// <param name="leds">Where each LED sits; the colours come back in this order.</param>
    /// <param name="random">Randomness (fixed seed in tests).</param>
    /// <param name="temperature">The CPU temperature in °C for the Temperature effect; null when not known.</param>
    public Animation(ZoneEffect effect, IReadOnlyList<LedPoint> leds, Random? random = null, Func<double?>? temperature = null)
    {
        _effect = effect;
        _leds = leds;
        _random = random ?? new Random();
        _temperature = temperature ?? (() => null);
        _colors = new Rgb[leds.Count];
        _level = new double[leds.Count];
        _tint = new Rgb[leds.Count];
        _cycleA = effect.Color1;
        _cycleB = effect.Color2;
        Step(0);
    }

    /// <summary>The colour of each LED, in the order the LEDs were given.</summary>
    public IReadOnlyList<Rgb> Colors => _colors;

    /// <summary>Slow, medium, fast as a rate: 1 = 0.5x, 2 = 1x, 3 = 2x.</summary>
    double Rate => Math.Pow(2, Math.Clamp(_effect.Speed, 1, 4) - 2);

    double Direction => _effect.Reverse ? -1 : 1;

    bool RandomColors => _effect.Colors == ColorMode.Random;

    Rgb RandomHue() => Rgb.FromHue(_random.NextDouble());

    /// <summary>The effect's colour for something new (a ripple, a drop, a star): its colour, or a random hue.</summary>
    Rgb Pick() => RandomColors ? RandomHue()
        : _effect.Colors == ColorMode.Dual && _random.Next(2) == 1 ? _effect.Color2 : _effect.Color1;

    /// <summary>A key or button went down at this LED: Reactive lights it, Ripple sends a ring out from it.</summary>
    public void Press(int led)
    {
        if (led < 0 || led >= _leds.Count)
            return;
        switch (_effect.Kind)
        {
            case EffectKind.Reactive:
                _level[led] = 1;
                _tint[led] = Pick();
                break;
            case EffectKind.Ripple:
                _ripples.Add((_leds[led], _time, Pick()));
                break;
        }
    }

    /// <summary>A press with no LED of its own (a mouse button): Reactive lights everything, Ripple starts in the middle.</summary>
    public void PressAll()
    {
        if (_effect.Kind == EffectKind.Ripple)
        {
            _ripples.Add((new LedPoint(0.5, 0.5), _time, Pick()));
            return;
        }
        var color = Pick();
        for (int i = 0; i < _leds.Count && _effect.Kind == EffectKind.Reactive; i++)
            (_level[i], _tint[i]) = (1, color);
    }

    /// <summary>Moves the effect on by dt seconds.</summary>
    public void Step(double dt)
    {
        _time += dt;
        double t = _time * Rate;
        for (int i = 0; i < _colors.Length; i++)
            _colors[i] = default;
        switch (_effect.Kind)
        {
            case EffectKind.Static:
                Fill(_effect.Color1);
                break;
            case EffectKind.Breathing:
                Breathe(t / 4, Math.Pow(Math.Sin(Math.PI * Fraction(t / 4)), 2));
                break;
            case EffectKind.Spectrum:
                Fill(Rgb.FromHue(t / 10));
                break;
            case EffectKind.Wave:
                for (int i = 0; i < _colors.Length; i++)
                    _colors[i] = Rgb.FromHue(_leds[i].X - Direction * t / 4);
                break;
            case EffectKind.Wheel:
                for (int i = 0; i < _colors.Length; i++)
                    _colors[i] = Rgb.FromHue(Math.Atan2(_leds[i].Y - 0.5, _leds[i].X - 0.5) / (2 * Math.PI) + Direction * t / 4);
                break;
            case EffectKind.Reactive:
                Fade(dt, seconds: 1.5 / Rate);
                break;
            case EffectKind.Starlight:
                Twinkle(dt);
                break;
            case EffectKind.Fire:
                Burn(dt);
                break;
            case EffectKind.Ripple:
                Ripple();
                break;
            case EffectKind.ColorShift:
                // Glides from one colour to the other and back.
                Fill(Rgb.Mix(_effect.Color1, _effect.Color2, 0.5 - 0.5 * Math.Cos(Math.PI * t / 3)));
                break;
            case EffectKind.ColorPulse:
                // Brightens and goes dark, taking the next colour each time; dark for a moment between pulses.
                Breathe(t / 2, Math.Max(0, Math.Sin(Math.PI * Math.Min(1, Fraction(t / 2) / 0.8))));
                break;
            case EffectKind.ColorWave:
                // A band of the first colour sweeps across a field of the second.
                for (int i = 0; i < _colors.Length; i++)
                {
                    double band = Fraction(_leds[i].X / 1.5 - Direction * t / 3);
                    _colors[i] = Rgb.Mix(_effect.Color2, _effect.Color1, Math.Max(0, 1 - Math.Abs(band - 0.5) * 4));
                }
                break;
            case EffectKind.Visor:
                Visor(t);
                break;
            case EffectKind.Rain:
                Rain(dt);
                break;
            case EffectKind.Watercolor:
                for (int i = 0; i < _colors.Length; i++)
                {
                    var p = _leds[i];
                    _colors[i] = Rgb.FromHue(t / 20 + 0.12 * Math.Sin(t / 2 + p.X * 3.1) + 0.12 * Math.Sin(t / 3 + p.Y * 2.3 + p.X));
                }
                break;
            case EffectKind.Marquee:
                // Lights chase along the LEDs: every third one lit, the rest in the second colour.
                {
                    var order = Order();
                    int shift = (int)Math.Floor(t * 4);
                    for (int n = 0; n < order.Count; n++)
                        _colors[order[n]] = ((n - (int)Direction * shift) % 3 + 3) % 3 == 0 ? _effect.Color1 : _effect.Color2;
                }
                break;
            case EffectKind.Sequential:
                // The LEDs take a colour one after another, then the next colour does the same.
                {
                    var order = Order();
                    double step = t * 6;
                    int round = (int)Math.Floor(step / order.Count);
                    int lit = (int)Math.Floor(step) % order.Count;
                    if (round != _cycle)
                    {
                        _cycle = round;
                        (_cycleB, _cycleA) = (_cycleA, RandomColors ? RandomHue() : round % 2 == 0 ? _effect.Color1 : _effect.Color2);
                        if (_effect.Colors == ColorMode.Single)
                            (_cycleA, _cycleB) = (_effect.Color1, default);
                    }
                    for (int n = 0; n < order.Count; n++)
                        _colors[order[n]] = n <= lit ? _cycleA : _cycleB;
                }
                break;
            case EffectKind.Strobing:
                Breathe(t / 0.3, Fraction(t / 0.3) < 0.35 ? 1 : 0);
                break;
            case EffectKind.Gradient:
                for (int i = 0; i < _colors.Length; i++)
                    _colors[i] = Rgb.Mix(_effect.Color1, _effect.Color2, _effect.Reverse ? 1 - _leds[i].X : _leds[i].X);
                break;
            case EffectKind.Heartbeat:
                {
                    // Two beats (lub-dub) every 1.2 s at medium speed.
                    double phase = Fraction(t / 1.2);
                    double beat = Math.Max(Pulse(phase, 0.0, 0.12), 0.7 * Pulse(phase, 0.2, 0.14));
                    Breathe(t / 1.2, beat);
                }
                break;
            case EffectKind.Temperature:
                {
                    // Cool colour at 40 °C and below, hot colour at 90 °C and above; the cool colour until a reading comes.
                    double heat = _temperature() is { } celsius ? (celsius - 40) / 50 : 0;
                    Fill(Rgb.Mix(_effect.Color1, _effect.Color2, heat));
                }
                break;
        }
    }

    static double Fraction(double x) => x - Math.Floor(x);

    /// <summary>A smooth bump of the given width starting at a point of the phase.</summary>
    static double Pulse(double phase, double start, double width) =>
        phase < start || phase > start + width ? 0 : Math.Sin(Math.PI * (phase - start) / width);

    void Fill(Rgb color)
    {
        for (int i = 0; i < _colors.Length; i++)
            _colors[i] = color;
    }

    /// <summary>One colour for the whole device at a level, the colour moving on each cycle: the first colour; first and
    /// second in turn; or a new random one.</summary>
    void Breathe(double cycles, double level)
    {
        int cycle = (int)Math.Floor(cycles);
        if (cycle != _cycle)
        {
            _cycle = cycle;
            _cycleA = _effect.Colors switch
            {
                ColorMode.Random => RandomHue(),
                ColorMode.Dual => cycle % 2 == 0 ? _effect.Color1 : _effect.Color2,
                _ => _effect.Color1,
            };
        }
        Fill(_cycleA.Dim(level));
    }

    /// <summary>The LEDs in reading order (left to right, then down), for effects that go along them.</summary>
    List<int> Order() => Enumerable.Range(0, _leds.Count).OrderBy(i => _leds[i].Y).ThenBy(i => _leds[i].X).ToList();

    void Fade(double dt, double seconds)
    {
        for (int i = 0; i < _colors.Length; i++)
        {
            _level[i] = Math.Max(0, _level[i] - dt / seconds);
            _colors[i] = _tint[i].Dim(_level[i]);
        }
    }

    void Twinkle(double dt)
    {
        // About one new star per LED every 2 s at medium speed; each fades over a second.
        for (int i = 0; i < _colors.Length; i++)
            if (_level[i] <= 0 && _random.NextDouble() < dt * Rate / 2)
            {
                _level[i] = 1;
                _tint[i] = Pick();
            }
        Fade(dt, 1 / Rate);
    }

    void Burn(double dt)
    {
        // Heat per LED: the bottom row (the whole device, on a line) flares at random; every LED cools, and on a grid
        // takes heat from the LED below it, so flames rise.
        double cool = dt * 1.6 * Rate;
        for (int i = 0; i < _colors.Length; i++)
        {
            var p = _leds[i];
            bool bottom = p.Y >= 0.99 || _leds.All(l => l.Y == p.Y);
            double below = bottom ? 0 : _level[Below(i)];
            double flare = bottom && _random.NextDouble() < dt * 6 * Rate ? 0.6 + 0.4 * _random.NextDouble() : 0;
            _level[i] = Math.Clamp(Math.Max(flare, (_level[i] * 0.5 + below * 0.42) * (1 - cool)), 0, 1);
            if (bottom && flare == 0)
                _level[i] = Math.Max(_level[i], 0.35 + 0.1 * _random.NextDouble());
            _colors[i] = HeatColor(_level[i]);
        }
    }

    int Below(int i)
    {
        var p = _leds[i];
        int best = i;
        double bestDistance = double.MaxValue;
        for (int j = 0; j < _leds.Count; j++)
        {
            var q = _leds[j];
            if (q.Y <= p.Y + 1e-9)
                continue;
            double d = (q.Y - p.Y) * 4 + Math.Abs(q.X - p.X);
            if (d < bestDistance)
                (best, bestDistance) = (j, d);
        }
        return best;
    }

    /// <summary>Black to red to orange to yellow, as heat rises.</summary>
    static Rgb HeatColor(double heat) => heat switch
    {
        < 0.4 => Rgb.Mix(default, new Rgb(200, 0, 0), heat / 0.4),
        < 0.75 => Rgb.Mix(new Rgb(200, 0, 0), new Rgb(255, 110, 0), (heat - 0.4) / 0.35),
        _ => Rgb.Mix(new Rgb(255, 110, 0), new Rgb(255, 210, 40), (heat - 0.75) / 0.25),
    };

    void Ripple()
    {
        // A ring per press, growing at 1.2 device widths a second (medium) and fading as it goes.
        _ripples.RemoveAll(r => (_time - r.Born) * Rate > 1.2);
        foreach (var (at, born, color) in _ripples)
        {
            double radius = (_time - born) * Rate * 1.2;
            double fade = 1 - radius / 1.5;
            for (int i = 0; i < _colors.Length; i++)
            {
                double distance = Math.Sqrt(Math.Pow(_leds[i].X - at.X, 2) + Math.Pow(_leds[i].Y - at.Y, 2));
                double level = Math.Max(0, 1 - Math.Abs(distance - radius) / 0.25) * fade;
                if (level > 0)
                    _colors[i] = Brighter(_colors[i], color.Dim(level));
            }
        }
    }

    static Rgb Brighter(Rgb a, Rgb b) => new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

    void Visor(double t)
    {
        // A bar going back and forth across the device with a short tail.
        double phase = Fraction(t / 2.5);
        double centre = phase < 0.5 ? phase * 2 : 2 - phase * 2;
        if (RandomColors && (int)Math.Floor(t / 1.25) != _cycle)
        {
            _cycle = (int)Math.Floor(t / 1.25);
            _cycleA = RandomHue();
        }
        var color = RandomColors ? _cycleA : _effect.Color1;
        for (int i = 0; i < _colors.Length; i++)
            _colors[i] = color.Dim(Math.Max(0, 1 - Math.Abs(_leds[i].X - centre) / 0.3));
    }

    void Rain(double dt)
    {
        // Drops start on the top row (any LED, on a line) and fall a row at a time, leaving a fading trail.
        bool grid = _leds.Any(l => l.Y != _leds[0].Y);
        double fall = 0.5 / Rate;
        int tick = (int)Math.Floor(_time / fall * 4);
        bool moved = tick != _tick;
        _tick = tick;
        if (moved && grid)
        {
            // Shift lit drops down: walk from the bottom up so a drop moves once.
            foreach (int i in Enumerable.Range(0, _leds.Count).OrderByDescending(i => _leds[i].Y))
                if (_level[i] >= 0.999)
                {
                    // A drop on the bottom row has landed: it fades where it is.
                    if (Below(i) is var b && b != i)
                    {
                        _level[b] = 1;
                        _tint[b] = _tint[i];
                    }
                    _level[i] = 0.7;
                }
        }
        if (moved && _random.NextDouble() < 0.5 * Rate)
        {
            var top = Enumerable.Range(0, _leds.Count).Where(i => !grid || _leds[i].Y <= 0.01).ToList();
            int start = top[_random.Next(top.Count)];
            _level[start] = 1;
            _tint[start] = Pick();
        }
        for (int i = 0; i < _colors.Length; i++)
        {
            if (_level[i] < 0.999)
                _level[i] = Math.Max(0, _level[i] - dt / (grid ? 0.35 : 0.8) * Rate);
            _colors[i] = _tint[i].Dim(_level[i]);
        }
    }
}
