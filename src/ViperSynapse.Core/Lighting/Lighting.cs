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
public sealed record LightingSettings(IReadOnlyDictionary<string, ZoneLighting> Zones, CustomFrame? Frame = null)
{
    public static LightingSettings None { get; } = new(new Dictionary<string, ZoneLighting>());

    [JsonIgnore]
    public bool IsEmpty => Zones.Count == 0;
}
