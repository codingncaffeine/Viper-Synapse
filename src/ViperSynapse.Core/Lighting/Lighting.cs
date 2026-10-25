using System.Globalization;
using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Lighting;

/// <summary>A colour as the device takes it.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb RazerGreen { get; } = new(0x44, 0xD6, 0x2C);

    public static Rgb Parse(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(
            byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>A fully saturated colour at a point round the colour wheel (0 red, 1/3 green, 2/3 blue, 1 red again).</summary>
    public static Rgb FromHue(double hue)
    {
        hue = (hue % 1 + 1) % 1 * 6;
        int sector = (int)hue;
        byte up = (byte)Math.Round((hue - sector) * 255), down = (byte)(255 - up);
        return sector switch
        {
            0 => new(255, up, 0),
            1 => new(down, 255, 0),
            2 => new(0, 255, up),
            3 => new(0, down, 255),
            4 => new(up, 0, 255),
            _ => new(255, 0, down),
        };
    }

    /// <summary>The colour a fraction t of the way from a to b.</summary>
    public static Rgb Mix(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>This colour at a fraction of its brightness.</summary>
    public Rgb Dim(double level) => Mix(default, this, level);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Synapse's quick effects.</summary>
public enum EffectKind
{
    Off,
    Static,
    Breathing,
    Spectrum,
    Wave,
    Reactive,
    Starlight,
    Wheel,
    /// <summary>Every LED its own colour: the per-key frame in <see cref="LightingSettings.Frame"/>.</summary>
    Custom,

    // Drawn by the service, frame by frame (see Animation): Razer Synapse's and Corsair iCUE's software effects.
    Fire,
    Ripple,
    ColorShift,
    ColorPulse,
    ColorWave,
    Visor,
    Rain,
    Watercolor,
    Marquee,
    Sequential,
    Strobing,
    Gradient,
    Heartbeat,
    Temperature,
}

/// <summary>How many colours an effect that takes colours uses: Synapse's "random", single and dual.</summary>
public enum ColorMode
{
    Single,
    Dual,
    Random,
}

/// <summary>One zone's effect. Speed is 1 (slow) to 3 or 4 depending on the effect; Direction picks the wave's way.</summary>
public sealed record ZoneEffect(EffectKind Kind, Rgb Color1, Rgb Color2, ColorMode Colors = ColorMode.Single, int Speed = 2, bool Reverse = false)
{
    public static ZoneEffect Default { get; } = new(EffectKind.Spectrum, Rgb.RazerGreen, new Rgb(0, 0, 255));
}

/// <summary>A zone's effect and brightness (0 to 100 percent).</summary>
public sealed record ZoneLighting(ZoneEffect Effect, int Brightness = 100);

/// <summary>Per-key colours: the device's LED grid, row by row (Rows x Columns entries).</summary>
public sealed record CustomFrame(int Rows, int Columns, IReadOnlyList<Rgb> Colors)
{
    public static CustomFrame Filled(int rows, int columns, Rgb color) => new(rows, columns, Enumerable.Repeat(color, rows * columns).ToList());

    public Rgb this[int row, int column] => Colors[row * Columns + column];

    public CustomFrame With(IEnumerable<(int Row, int Column)> cells, Rgb color)
    {
        var colors = Colors.ToArray();
        foreach (var (r, c) in cells)
            if (r >= 0 && r < Rows && c >= 0 && c < Columns)
                colors[r * Columns + c] = color;
        return this with { Colors = colors };
    }
}

/// <summary>A profile's lighting: zone name (main, logo, scroll, right, ...) to its settings, and the per-key frame a
/// Custom effect shows. An empty map leaves the device's lighting alone.</summary>
/// <param name="Zones">Each zone's effect and brightness.</param>
/// <param name="Frame">The per-key colours a Custom effect shows.</param>
/// <param name="Off">The Brightness switch turned off: every zone dark, effects kept for when it comes back on.</param>
/// <param name="OffWhenDisplayOff">Go dark while the screen is locked or blanked.</param>
/// <param name="OffWhenIdleMinutes">Go dark after this many minutes without input; 0 never.</param>
public sealed record LightingSettings(
    IReadOnlyDictionary<string, ZoneLighting> Zones,
    CustomFrame? Frame = null,
    bool Off = false,
    bool OffWhenDisplayOff = false,
    int OffWhenIdleMinutes = 0)
{
    public static LightingSettings None { get; } = new(new Dictionary<string, ZoneLighting>());

    [JsonIgnore]
    public bool IsEmpty => Zones.Count == 0;
}
