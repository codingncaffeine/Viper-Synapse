namespace ViperSynapse.Core.Engine;

/// <summary>Where the engine's output goes: the virtual keyboard and mouse (uinput), or a recorder in tests.
/// Calls arrive from the input thread and from macro and turbo tasks; implementations serialise them.</summary>
public interface IInputSink
{
    /// <summary>A key or button: 1 down, 0 up, 2 auto-repeat.</summary>
    void Key(ushort code, int value);

    /// <summary>A relative axis: pointer motion or the wheel (REL_* codes).</summary>
    void Relative(ushort axis, int value);

    /// <summary>Ends a group of events that belong together (SYN_REPORT).</summary>
    void Sync();
}

/// <summary>What the engine asks of the program around it.</summary>
public interface IEngineHost
{
    void Launch(string command);
    void SwitchProfile(Bindings.CycleDirection direction, Guid? profileId);
    void SwitchKeymap(Bindings.CycleDirection direction, int keymap);
    Bindings.Macro? FindMacro(Guid id);
    /// <summary>A macro or turbo task failed; the engine keeps running.</summary>
    void Report(Exception error);
}

public static class RelativeAxes
{
    public const ushort X = 0, Y = 1, HWheel = 6, Wheel = 8, WheelHiRes = 11, HWheelHiRes = 12;
}
