using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Bindings;

/// <summary>One step of a macro, as Synapse's macro editor lists them.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(KeyStep), "key")]
[JsonDerivedType(typeof(DelayStep), "delay")]
[JsonDerivedType(typeof(ScrollStep), "scroll")]
[JsonDerivedType(typeof(TextStep), "text")]
[JsonDerivedType(typeof(RunStep), "run")]
public abstract record MacroStep;

/// <summary>A key or mouse button going down or up (mouse buttons are BTN_* codes).</summary>
public sealed record KeyStep(ushort Code, bool Down) : MacroStep;

public sealed record DelayStep(int Milliseconds) : MacroStep;

/// <summary>Wheel clicks: positive is up (or right when horizontal).</summary>
public sealed record ScrollStep(int Clicks, bool Horizontal = false) : MacroStep;

public sealed record TextStep(string Text) : MacroStep;

public sealed record RunStep(string Command) : MacroStep;

/// <summary>How a recording turns the time between events into delays: Synapse's three choices.</summary>
public enum RecordDelayMode
{
    /// <summary>Keep the gaps as they happened.</summary>
    Recorded,
    /// <summary>Replace every gap with one fixed delay.</summary>
    Default,
    /// <summary>No delays at all.</summary>
    None,
}

public sealed record Macro(Guid Id, string Name, IReadOnlyList<MacroStep> Steps)
{
    public static Macro Create(string name, IReadOnlyList<MacroStep> steps) => new(Guid.NewGuid(), name, steps);
}
