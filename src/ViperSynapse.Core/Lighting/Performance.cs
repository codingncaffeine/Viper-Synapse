namespace ViperSynapse.Core.Lighting;

/// <summary>One sensitivity stage. X and Y differ only when the user unties them.</summary>
public sealed record DpiStage(int X, int Y);

/// <summary>A mouse's Performance page: up to five stages, which one is active, and the polling rate in Hz
/// (0 = leave it as it is).</summary>
public sealed record PerformanceSettings(IReadOnlyList<DpiStage> Stages, int ActiveStage, int PollingRate)
{
    public const int MaxStages = 5;

    public static PerformanceSettings Default { get; } =
        new([new(400, 400), new(800, 800), new(1600, 1600), new(3200, 3200), new(6400, 6400)], 2, 0);
}

/// <summary>A wireless mouse's Power page: sleep after this many seconds idle, warn at this battery percentage.</summary>
public sealed record PowerSettings(int SleepSeconds, int LowBatteryPercent)
{
    public static PowerSettings Default { get; } = new(300, 15);
}

/// <summary>A mouse's Calibration page: the lift-off distance through Smart Tracking. Level 0, 1, 2 = 1, 2, 3 mm;
/// -1 = the sensor's own default.</summary>
public sealed record CalibrationSettings(int LiftOffLevel)
{
    public static CalibrationSettings Default { get; } = new(-1);
}

/// <summary>A mouse's Scroll Wheel card. <see cref="LinesPerNotch"/> is applied in software (each notch of the wheel
/// scrolls that many times as far); acceleration and Smart-Reel are the firmware's own, on mice that have them.</summary>
public sealed record ScrollSettings(int LinesPerNotch = 1, bool Acceleration = false, bool SmartReel = false)
{
    public const int MaxLinesPerNotch = 10;

    public static ScrollSettings Default { get; } = new();
}
