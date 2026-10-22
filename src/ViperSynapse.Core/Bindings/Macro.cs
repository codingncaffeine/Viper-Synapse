using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Bindings;

/// <summary>One step of a macro, as Synapse's macro editor lists them.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(KeyStep), "key")]
[JsonDerivedType(typeof(DelayStep), "delay")]
[JsonDerivedType(typeof(RandomDelayStep), "random-delay")]
[JsonDerivedType(typeof(ScrollStep), "scroll")]
[JsonDerivedType(typeof(TextStep), "text")]
[JsonDerivedType(typeof(RunStep), "run")]
[JsonDerivedType(typeof(LaunchStep), "launch")]
[JsonDerivedType(typeof(MacroCallStep), "macro")]
[JsonDerivedType(typeof(LoopStartStep), "loop-start")]
[JsonDerivedType(typeof(LoopEndStep), "loop-end")]
public abstract record MacroStep;

/// <summary>A key or mouse button going down or up (mouse buttons are BTN_* codes).</summary>
public sealed record KeyStep(ushort Code, bool Down) : MacroStep;

public sealed record DelayStep(int Milliseconds) : MacroStep;

/// <summary>A delay drawn afresh between the two bounds each time the macro plays (Synapse's randomized delay).</summary>
public sealed record RandomDelayStep(int MinMilliseconds, int MaxMilliseconds) : MacroStep;

/// <summary>Wheel clicks: positive is up (or right when horizontal).</summary>
public sealed record ScrollStep(int Clicks, bool Horizontal = false) : MacroStep;

public sealed record TextStep(string Text) : MacroStep;

/// <summary>Runs a command line (Synapse's Run Command).</summary>
public sealed record RunStep(string Command) : MacroStep;

/// <summary>Opens a program, file or web address with the desktop's default handler (Synapse's Launch).</summary>
public sealed record LaunchStep(string Target) : MacroStep;

/// <summary>Plays another macro, start to end, at this point.</summary>
public sealed record MacroCallStep(Guid MacroId) : MacroStep;

/// <summary>The steps up to the matching <see cref="LoopEndStep"/> play this many times.</summary>
public sealed record LoopStartStep(int Count) : MacroStep;

public sealed record LoopEndStep : MacroStep;

/// <summary>How a recording turns the time between events into delays: Synapse's delay settings.</summary>
public enum RecordDelayMode
{
    /// <summary>Keep the gaps as they happened.</summary>
    Recorded,
    /// <summary>Replace every gap with one fixed delay.</summary>
    Default,
    /// <summary>No delays at all.</summary>
    None,
    /// <summary>Every gap a delay drawn between two bounds each time it plays.</summary>
    Randomized,
}

/// <summary>Standard plays the whole macro on a press; Sequence plays the next action on each press (actions are
/// the stretches between delays).</summary>
public enum MacroType
{
    Standard,
    Sequence,
}

public sealed record Macro(Guid Id, string Name, IReadOnlyList<MacroStep> Steps, MacroType Type = MacroType.Standard)
{
    public static Macro Create(string name, IReadOnlyList<MacroStep> steps) => new(Guid.NewGuid(), name, steps);

    /// <summary>The actions of a sequence macro: the steps split at each delay (the delays and any loop markers
    /// dropped).</summary>
    public IReadOnlyList<IReadOnlyList<MacroStep>> Actions()
    {
        var actions = new List<IReadOnlyList<MacroStep>>();
        var current = new List<MacroStep>();
        foreach (var step in Steps)
        {
            // Loops mean nothing to a sequence played one action per press.
            if (step is LoopStartStep or LoopEndStep)
                continue;
            if (step is DelayStep or RandomDelayStep)
            {
                if (current.Count > 0)
                    actions.Add(current);
                current = [];
            }
            else
            {
                current.Add(step);
            }
        }
        if (current.Count > 0)
            actions.Add(current);
        return actions;
    }

    /// <summary>How long the macro takes to play, counting delays (random ones at their middle) and loops.</summary>
    public TimeSpan Duration
    {
        get
        {
            double ms = 0;
            var multipliers = new Stack<int>();
            int factor = 1;
            foreach (var step in Steps)
            {
                switch (step)
                {
                    case DelayStep d: ms += d.Milliseconds * factor; break;
                    case RandomDelayStep r: ms += (r.MinMilliseconds + r.MaxMilliseconds) / 2.0 * factor; break;
                    case LoopStartStep l:
                        multipliers.Push(factor);
                        factor *= Math.Max(1, l.Count);
                        break;
                    case LoopEndStep when multipliers.Count > 0:
                        factor = multipliers.Pop();
                        break;
                }
            }
            return TimeSpan.FromMilliseconds(ms);
        }
    }
}
