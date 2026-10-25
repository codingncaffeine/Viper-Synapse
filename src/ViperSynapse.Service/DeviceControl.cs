using System.Text.Json;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;

namespace ViperSynapse.Service;

/// <summary>What the Performance and Power pages show before the user changes anything: the device's own values.</summary>
public sealed record DeviceState(
    IReadOnlyList<DpiStage>? Stages,
    int ActiveStage,
    int PollingRate,
    int? BatteryPercent,
    bool? Charging,
    int LiftOffLevel = -1,
    int SleepSeconds = 0,
    int LowBatteryPercent = 0,
    bool? ScrollAcceleration = null,
    bool? SmartReel = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceState))]
internal sealed partial class DeviceStateJson : JsonSerializerContext;

/// <summary>
/// One device's control interface, kept open while it is plugged in: sends a profile's lighting, sensitivity and
/// power settings, and reads the device's state. A setting is sent only when it differs from what was sent last,
/// so saving a key binding does not restart the lighting animation. Failures are reported, never thrown at the
/// caller: a device that refuses one command keeps the rest.
/// </summary>
public sealed class DeviceControl(RazerTransport transport, byte transactionId, DeviceModel? model) : IDisposable
{
    readonly Lock _gate = new();
    string? _sentLighting, _sentPerformance, _sentPower, _sentCalibration, _sentScroll;
    int _sentKeymap = -1;

    public string? LastProblem { get; private set; }

    /// <param name="profile">The profile to bring the device in line with.</param>
    /// <param name="keymap">The keymap in use (0-based): a keypad with keymaps of its own is switched to it.</param>
    public void Apply(Profile profile, int keymap = 0)
    {
        lock (_gate)
        {
            LastProblem = null;
            if (model?.Has("keymaps") == true && keymap != _sentKeymap)
            {
                _sentKeymap = keymap;
                Send(DeviceCommands.SelectKeymap(transactionId, keymap + 1), "keymap");
            }
            if (profile.Lighting is { IsEmpty: false } current)
                _lighting = current;
            if (profile.Lighting is { IsEmpty: false } lighting && Changed(ref _sentLighting, lighting))
                SendLighting(lighting);
            if (profile.Performance is { } performance && (model?.Has("dpi") == true || model?.Has("poll_rate") == true) && Changed(ref _sentPerformance, performance))
                SendPerformance(performance);
            if (profile.Power is { } power && model?.Has("idle_time") == true && Changed(ref _sentPower, power))
                SendPower(power);
            if (profile.Calibration is { } calibration && model?.Has("lift_off") == true && Changed(ref _sentCalibration, calibration))
                SendCalibration(calibration);
            // Only the firmware's part: the scroll speed is the remap engine's.
            if (profile.Scroll is { } scroll && model?.Has("scroll_wheel") == true && Changed(ref _sentScroll, scroll with { LinesPerNotch = 1 }))
            {
                Send(DeviceCommands.ScrollAcceleration(transactionId, scroll.Acceleration), "scroll acceleration");
                Send(DeviceCommands.SmartReel(transactionId, scroll.SmartReel), "Smart-Reel");
            }
        }
    }

    static bool Changed<T>(ref string? sent, T value)
    {
        var now = JsonSerializer.Serialize(value, ControlJson.Options);
        if (now == sent)
            return false;
        sent = now;
        return true;
    }

    void SendLighting(LightingSettings lighting)
    {
        string type = model?.Type ?? "keyboard";
        bool frameSent = false;
        // Stopped before anything goes out, so a last drawn frame cannot land on top of a firmware effect.
        _software.Stop();
        var drawn = lighting.Zones.Where(z => _software.Draws(z.Key, z.Value.Effect)).ToDictionary(z => z.Key, z => z.Value.Effect);
        foreach (var (zone, settings) in lighting.Zones)
        {
            byte led = DeviceCommands.LedId(zone, type);
            if (drawn.ContainsKey(zone))
            {
                // Drawn by the service (below); only its brightness goes to the device.
            }
            else if (settings.Effect.Kind == EffectKind.Custom)
            {
                // One frame covers the whole device: send it, then switch to it, once whatever zones ask for it.
                if (frameSent || lighting.Frame == null)
                    continue;
                foreach (var row in DeviceCommands.CustomFrame(transactionId, lighting.Frame))
                    Send(row, "per-key colours");
                Send(DeviceCommands.CustomFrameEffect(transactionId), "per-key effect");
                frameSent = true;
            }
            else
            {
                SendBoth(store => DeviceCommands.Effect(transactionId, led, settings.Effect, store), $"{zone} effect");
            }
            if (model == null || !model.Zones.TryGetValue(zone, out var known) || known.Brightness)
                SendBoth(store => DeviceCommands.Brightness(transactionId, led, lighting.Off || _dimmed ? 0 : settings.Brightness, store), $"{zone} brightness");
        }
        _software.Paused = lighting.Off || _dimmed;
        _software.Show(drawn, type);
    }

    readonly SoftwareLighting _software = new(transport, transactionId, model);

    /// <summary>A key or button went down (its source code): for the drawn Reactive and Ripple effects. Any thread.</summary>
    public void Press(ushort source) => _software.Press(source);

    bool _dimmed;
    LightingSettings? _lighting;

    /// <summary>The profile's switch-off rules, for the idle watcher.</summary>
    public LightingSettings? Lighting
    {
        get { lock (_gate) return _lighting; }
    }

    /// <summary>Darkens the device (the screen went off, the user went idle) or brings its brightness back, without
    /// touching the effects.</summary>
    public void SetDimmed(bool dimmed)
    {
        lock (_gate)
        {
            if (dimmed == _dimmed || _lighting is not { } lighting)
                return;
            _dimmed = dimmed;
            _software.Paused = lighting.Off || dimmed;
            string type = model?.Type ?? "keyboard";
            foreach (var (zone, settings) in lighting.Zones)
                if (model == null || !model.Zones.TryGetValue(zone, out var known) || known.Brightness)
                    SendBoth(store => DeviceCommands.Brightness(transactionId, DeviceCommands.LedId(zone, type), lighting.Off || dimmed ? 0 : settings.Brightness, store), $"{zone} brightness");
        }
    }

    /// <summary>The keymap the keypad is on now (0-based), read from it; -1 when it does not say.</summary>
    public int ReadKeymap()
    {
        lock (_gate)
        {
            try
            {
                if (ReadRaw(0x02, 0xB7) is { } answer && answer.Arguments[0] is >= 1 and <= 8)
                    return _sentKeymap = answer.Arguments[0] - 1;
            }
            catch (IOException) { }
            return -1;
        }
    }

    /// <summary>The keymap the device is on, as the keypad reports it moving (its keymap button).</summary>
    public void KeymapChangedOnDevice(int keymap)
    {
        lock (_gate)
            _sentKeymap = keymap;
    }

    void SendPerformance(PerformanceSettings p)
    {
        // No stage above what the sensor can do, whatever the settings file says.
        if (model?.DpiMax is int max && max > 0)
            p = p with { Stages = p.Stages.Select(s => new DpiStage(Math.Min(s.X, max), Math.Min(s.Y, max))).ToList() };
        if (p.Stages.Count > 0 && model?.Has("dpi") == true)
        {
            if (model?.Has("dpi_stages") == true)
                Send(DeviceCommands.DpiStages(transactionId, p, model.Has("dpi_stage_third")), "sensitivity stages");
            var active = p.Stages[Math.Clamp(p.ActiveStage, 0, p.Stages.Count - 1)];
            Send(DeviceCommands.Dpi(transactionId, active.X, active.Y), "sensitivity");
        }
        if (p.PollingRate > 0 && model?.Has("poll_rate") == true)
        {
            bool hyper = model.PollRates.Any(r => r > 1000);
            Send(hyper ? DeviceCommands.HyperPollingRate(transactionId, p.PollingRate) : DeviceCommands.PollingRate(transactionId, p.PollingRate), "polling rate");
        }
    }

    void SendCalibration(CalibrationSettings c)
    {
        Send(DeviceCommands.CalibrationMode(transactionId, false), "calibration mode");
        if (c.LiftOffLevel >= 0)
        {
            Send(DeviceCommands.SmartTracking(transactionId, c.LiftOffLevel), "lift-off distance");
            return;
        }
        // Back to the sensor's own mode 0. The Naga answers this write with Failure yet applies it, so the result is
        // judged by reading the mode back, not by the answer.
        try
        {
            transport.Send(DeviceCommands.TrackingModeDefault(transactionId));
            if (ReadRaw(0x0B, 0x8B, 0x00, 0x04) is { } mode && mode.Arguments[2] != 0x00)
                LastProblem = "The sensor did not go back to its default tracking.";
        }
        catch (IOException e)
        {
            LastProblem = $"Could not reset the sensor: {e.Message}";
        }
    }

    void SendPower(PowerSettings p)
    {
        Send(DeviceCommands.IdleTime(transactionId, p.SleepSeconds), "sleep timer");
        if (model?.Has("low_battery_threshold") == true)
            Send(DeviceCommands.LowBatteryThreshold(transactionId, p.LowBatteryPercent), "low battery threshold");
    }

    /// <summary>Lighting to the device's storage, then to its live slot. The Tartarus V2 Pro keeps the two apart and shows
    /// the live one: an effect stored alone is saved but not shown while the per-key frame (sent live) is up, and the
    /// keys stay on the frame. Mice with one slot (the Naga, the Basilisk) answer both alike.</summary>
    void SendBoth(Func<byte, RazerReport> build, string what)
    {
        Send(build(DeviceCommands.VarStore), what);
        Send(build(DeviceCommands.NoStore), what);
    }

    void Send(RazerReport request, string what)
    {
        try
        {
            var answer = transport.Send(request);
            if (answer.Status != ReportStatus.Successful)
                LastProblem = $"The device refused the {what} ({answer.Status}).";
        }
        catch (IOException e)
        {
            LastProblem = $"Could not send the {what}: {e.Message}";
        }
    }

    /// <summary>The device's sensitivity, polling rate and battery as it reports them now.</summary>
    public DeviceState ReadState()
    {
        lock (_gate)
        {
            IReadOnlyList<DpiStage>? stages = null;
            int active = 0, rate = 0;
            int? battery = null;
            bool? charging = null;
            int liftOff = -1, sleep = 0, lowBattery = 0;
            bool? acceleration = null, smartReel = null;
            try
            {
                if (model?.Has("poll_rate") == true)
                {
                    if (model.PollRates.Any(r => r > 1000))
                    {
                        var hyper = transport.Send(DeviceCommands.GetHyperPollingRate(transactionId));
                        if (hyper.Status == ReportStatus.Successful)
                            rate = DeviceCommands.ReadHyperPollingRate(hyper);
                    }
                    else if (Read(Commands.GetPollingRate) is { } poll)
                    {
                        rate = DeviceCommands.ReadPollingRate(poll);
                    }
                }
                if (model?.Has("dpi_stages") == true && Read(Commands.GetDpiStages) is { } table)
                {
                    var read = DeviceCommands.ReadStages(table, rate);
                    (stages, active) = (read.Stages, read.ActiveStage);
                }
                else if (model?.Has("dpi") == true && Read(Commands.GetDpi) is { } dpi)
                {
                    var a = dpi.Arguments;
                    stages = [new DpiStage((a[1] << 8) | a[2], (a[3] << 8) | a[4])];
                }
                if (model?.Has("battery") == true && Read(Commands.GetBattery) is { } level)
                    battery = DeviceCommands.ReadBatteryPercent(level);
                if (model?.Has("charging") == true && Read(Commands.GetCharging) is { } state)
                    charging = state.Arguments[1] != 0;
                if (model?.Has("lift_off") == true && ReadRaw(0x0B, 0x8B, 0x00, 0x04) is { } tracking)
                    liftOff = DeviceCommands.ReadLiftOffLevel(tracking);
                if (model?.Has("idle_time") == true && ReadRaw(0x07, 0x83) is { } idle)
                    sleep = (idle.Arguments[0] << 8) | idle.Arguments[1];
                if (model?.Has("low_battery_threshold") == true && ReadRaw(0x07, 0x81) is { } low)
                    lowBattery = (int)Math.Round(low.Arguments[0] / 2.55);
                if (model?.Has("scroll_wheel") == true)
                {
                    if (ReadRaw(0x02, 0x96, DeviceCommands.VarStore) is { } accel)
                        acceleration = accel.Arguments[1] != 0;
                    if (ReadRaw(0x02, 0x97, DeviceCommands.VarStore) is { } reel)
                        smartReel = reel.Arguments[1] != 0;
                }
            }
            catch (IOException e)
            {
                LastProblem = e.Message;
            }
            return new DeviceState(stages, active, rate, battery, charging, liftOff, sleep, lowBattery, acceleration, smartReel);
        }
    }

    RazerReport? ReadRaw(byte commandClass, byte id, params byte[] arguments)
    {
        var answer = transport.Send(RazerReport.Create(transactionId, commandClass, id, arguments));
        return answer.Status == ReportStatus.Successful ? answer : null;
    }

    RazerReport? Read(Commands.Command command)
    {
        var answer = transport.Send(Commands.Request(transactionId, command));
        return answer.Status == ReportStatus.Successful ? answer : null;
    }

    public void Dispose()
    {
        _software.Dispose();
        transport.Dispose();
    }
}

static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = ControlJsonContext.Default };
}

[JsonSerializable(typeof(LightingSettings))]
[JsonSerializable(typeof(PerformanceSettings))]
[JsonSerializable(typeof(ScrollSettings))]
[JsonSerializable(typeof(PowerSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;
