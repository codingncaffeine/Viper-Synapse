using ViperSynapse.Core.Bindings;

namespace ViperSynapse.Core.Engine;

/// <summary>Plays macro steps into the sink. Keys the macro leaves down when it is stopped are let go, so a
/// cancelled macro never leaves a key stuck.</summary>
public sealed class MacroRunner(IInputSink sink, IEngineHost host, TimeProvider time)
{
    /// <summary>A macro may play another, which may play another: this deep and no deeper, so two macros that call
    /// each other stop instead of running forever.</summary>
    public const int MaxNesting = 8;

    /// <summary>Plays <paramref name="steps"/> once. Returns normally when cancelled.</summary>
    public async Task PlayAsync(IReadOnlyList<MacroStep> steps, CancellationToken cancel) => await PlayAsync(steps, cancel, 0).ConfigureAwait(false);

    async Task PlayAsync(IReadOnlyList<MacroStep> steps, CancellationToken cancel, int depth)
    {
        var down = new HashSet<ushort>();
        // Open loops: where each starts and how many passes it has left.
        var loops = new Stack<(int Start, int Left)>();
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                switch (steps[i])
                {
                    case KeyStep key:
                        Press(key, down);
                        break;
                    case DelayStep delay when delay.Milliseconds > 0:
                        await Task.Delay(TimeSpan.FromMilliseconds(delay.Milliseconds), time, cancel).ConfigureAwait(false);
                        break;
                    case RandomDelayStep random:
                        int lo = Math.Min(random.MinMilliseconds, random.MaxMilliseconds), hi = Math.Max(random.MinMilliseconds, random.MaxMilliseconds);
                        int ms = Random.Shared.Next(lo, hi + 1);
                        if (ms > 0)
                            await Task.Delay(TimeSpan.FromMilliseconds(ms), time, cancel).ConfigureAwait(false);
                        break;
                    case ScrollStep scroll:
                        sink.Relative(scroll.Horizontal ? RelativeAxes.HWheel : RelativeAxes.Wheel, scroll.Clicks);
                        sink.Relative(scroll.Horizontal ? RelativeAxes.HWheelHiRes : RelativeAxes.WheelHiRes, scroll.Clicks * 120);
                        sink.Sync();
                        break;
                    case TextStep text:
                        foreach (var key in TextTyper.Steps(text.Text))
                            Press(key, down);
                        break;
                    case RunStep run:
                        host.Launch(run.Command);
                        break;
                    case LaunchStep launch:
                        host.Open(launch.Target);
                        break;
                    case MacroCallStep call when depth < MaxNesting && host.FindMacro(call.MacroId) is { } inner:
                        await PlayAsync(inner.Steps, cancel, depth + 1).ConfigureAwait(false);
                        break;
                    case LoopStartStep loop:
                        loops.Push((i, Math.Max(1, loop.Count) - 1));
                        break;
                    case LoopEndStep when loops.Count > 0:
                        var (start, left) = loops.Pop();
                        if (left > 0)
                        {
                            loops.Push((start, left - 1));
                            i = start;   // the loop's first step comes next
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var code in down)
                sink.Key(code, 0);
            if (down.Count > 0)
                sink.Sync();
        }
    }

    void Press(KeyStep key, HashSet<ushort> down)
    {
        sink.Key(key.Code, key.Down ? 1 : 0);
        sink.Sync();
        if (key.Down) down.Add(key.Code);
        else down.Remove(key.Code);
    }

    /// <summary>Plays a macro the way its binding says, until it ends or <paramref name="cancel"/> fires.</summary>
    public async Task PlayAsync(Macro macro, MacroPlayback playback, int times, CancellationToken cancel)
    {
        int runs = playback switch
        {
            MacroPlayback.Once => 1,
            MacroPlayback.Times => Math.Max(1, times),
            _ => int.MaxValue,
        };
        // A macro with no delay that repeats forever would spin a core; give each pass a breath.
        bool repeating = runs > 1 && !macro.Steps.OfType<DelayStep>().Any(d => d.Milliseconds > 0);
        for (int i = 0; i < runs && !cancel.IsCancellationRequested; i++)
        {
            await PlayAsync(macro.Steps, cancel).ConfigureAwait(false);
            if (repeating && i + 1 < runs)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(1), time, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
