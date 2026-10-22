using ViperSynapse.Core.Bindings;

namespace ViperSynapse.Core.Engine;

/// <summary>
/// Turns an analog stick position into the directions it presses, the way Synapse's 4-way and 8-way thumbstick modes
/// do. A direction goes down past <see cref="PressAt"/> of full travel and comes up again below <see cref="ReleaseAt"/>,
/// so a stick resting at the threshold does not chatter. 4-way presses only the stronger axis; 8-way presses both
/// axes at once on a diagonal.
/// </summary>
public sealed class StickDirections(ThumbstickMode mode)
{
    public const double PressAt = 0.5, ReleaseAt = 0.35;

    readonly HashSet<ushort> _down = [];

    public ThumbstickMode Mode { get; } = mode;

    public IReadOnlySet<ushort> Down => _down;

    /// <summary>Takes the stick at (x, y), each -1 to 1 with y negative upwards (evdev\x27s convention), and returns the
    /// directions that changed: (source, true) when pressed, (source, false) when released.</summary>
    public IReadOnlyList<(ushort Source, bool Pressed)> Move(double x, double y)
    {
        var want = new HashSet<ushort>();
        void Axis(double value, ushort negative, ushort positive)
        {
            ushort? current = _down.Contains(negative) ? negative : _down.Contains(positive) ? positive : null;
            double held = current == negative ? -value : current == positive ? value : 0;
            if (current is ushort c && held >= ReleaseAt)
                want.Add(c);
            else if (value <= -PressAt)
                want.Add(negative);
            else if (value >= PressAt)
                want.Add(positive);
        }

        if (Mode == ThumbstickMode.FourWay)
        {
            // The stronger axis wins; the weaker one is let go, so a diagonal never presses two directions.
            if (Math.Abs(x) >= Math.Abs(y))
                Axis(x, SourceKeys.StickLeft, SourceKeys.StickRight);
            else
                Axis(y, SourceKeys.StickUp, SourceKeys.StickDown);
        }
        else if (Mode == ThumbstickMode.EightWay)
        {
            Axis(x, SourceKeys.StickLeft, SourceKeys.StickRight);
            Axis(y, SourceKeys.StickUp, SourceKeys.StickDown);
        }

        var changes = new List<(ushort, bool)>();
        foreach (var gone in _down.Where(d => !want.Contains(d)).ToList())
        {
            _down.Remove(gone);
            changes.Add((gone, false));
        }
        foreach (var added in want.Where(_down.Add))
            changes.Add((added, true));
        return changes;
    }

    /// <summary>Lets every direction go (the session is ending or the mode changed).</summary>
    public IReadOnlyList<(ushort Source, bool Pressed)> Release()
    {
        var changes = _down.Select(d => (d, false)).ToList();
        _down.Clear();
        return changes;
    }
}
