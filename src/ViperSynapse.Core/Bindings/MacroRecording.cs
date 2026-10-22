namespace ViperSynapse.Core.Bindings;

/// <summary>One event caught while recording: a key or button (value 1 down, 0 up) or a wheel detent.</summary>
public readonly record struct RecordedEvent(TimeSpan Time, ushort Code, int Value, bool IsWheel = false, bool Horizontal = false);

/// <summary>Turns what was caught while recording into macro steps.</summary>
public static class MacroRecording
{
    /// <summary>
    /// Builds the steps for <paramref name="mode"/>. Auto-repeats are dropped (playback holds a key as long as it
    /// was held). A key released without having been pressed in the recording (it was down when recording
    /// started) is dropped too, so a macro never lets go of a key it did not press. Delays below a
    /// millisecond are not worth a step.
    /// </summary>
    public static IReadOnlyList<MacroStep> ToSteps(IEnumerable<RecordedEvent> events, RecordDelayMode mode, int defaultDelayMs = 50, int randomMinMs = 0, int randomMaxMs = 0)
    {
        var steps = new List<MacroStep>();
        var down = new HashSet<ushort>();
        TimeSpan? last = null;
        foreach (var e in events.OrderBy(e => e.Time))
        {
            if (!e.IsWheel)
            {
                if (e.Value == 2)
                    continue;
                if (e.Value == 1 && !down.Add(e.Code))
                    continue;
                if (e.Value == 0 && !down.Remove(e.Code))
                    continue;
            }
            if (last is TimeSpan previous)
            {
                if (mode == RecordDelayMode.Randomized)
                {
                    steps.Add(new RandomDelayStep(Math.Max(0, randomMinMs), Math.Max(randomMinMs, randomMaxMs)));
                    last = e.Time;
                    steps.Add(e.IsWheel ? new ScrollStep(e.Value, e.Horizontal) : new KeyStep(e.Code, e.Value == 1));
                    continue;
                }
                int gap = mode switch
                {
                    RecordDelayMode.Recorded => (int)Math.Round((e.Time - previous).TotalMilliseconds),
                    RecordDelayMode.Default => defaultDelayMs,
                    _ => 0,
                };
                if (gap >= 1)
                    steps.Add(new DelayStep(gap));
            }
            last = e.Time;
            steps.Add(e.IsWheel ? new ScrollStep(e.Value, e.Horizontal) : new KeyStep(e.Code, e.Value == 1));
        }
        return steps;
    }
}
