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
    bool? Charging);

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
    string? _sentLighting, _sentPerformance, _sentPower;

    public string? LastProblem { get; private set; }

    public void Apply(Profile profile)
    {
        lock (_gate)
        {
            LastProblem = null;
            if (profile.Lighting is { IsEmpty: false } lighting && Changed(ref _sentLighting, lighting))
                SendLighting(lighting);
            if (profile.Performance is { } performance && model?.Has("dpi") == true && Changed(ref _sentPerformance, performance))
                SendPerformance(performance);
            if (profile.Power is { } power && model?.Has("idle_time") == true && Changed(ref _sentPower, power))
                SendPower(power);
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
        foreach (var (zone, settings) in lighting.Zones)
        {
            byte led = DeviceCommands.LedId(zone, type);
            Send(DeviceCommands.Effect(transactionId, led, settings.Effect), $"{zone} effect");
            if (model == null || !model.Zones.TryGetValue(zone, out var known) || known.Brightness)
                Send(DeviceCommands.Brightness(transactionId, led, settings.Brightness), $"{zone} brightness");
        }
    }

    void SendPerformance(PerformanceSettings p)
    {
        if (p.Stages.Count > 0)
        {
            if (model?.Has("dpi_stages") == true)
                Send(DeviceCommands.DpiStages(transactionId, p), "sensitivity stages");
            var active = p.Stages[Math.Clamp(p.ActiveStage, 0, p.Stages.Count - 1)];
            Send(DeviceCommands.Dpi(transactionId, active.X, active.Y), "sensitivity");
        }
        if (p.PollingRate > 0 && model?.Has("poll_rate") == true)
        {
            bool hyper = model.PollRates.Any(r => r > 1000);
            Send(hyper ? DeviceCommands.HyperPollingRate(transactionId, p.PollingRate) : DeviceCommands.PollingRate(transactionId, p.PollingRate), "polling rate");
        }
    }

    void SendPower(PowerSettings p)
    {
        Send(DeviceCommands.IdleTime(transactionId, p.SleepSeconds), "sleep timer");
        if (model?.Has("low_battery_threshold") == true)
            Send(DeviceCommands.LowBatteryThreshold(transactionId, p.LowBatteryPercent), "low battery threshold");
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
            try
            {
                if (model?.Has("poll_rate") == true && Read(Commands.GetPollingRate) is { } poll)
                    rate = DeviceCommands.ReadPollingRate(poll);
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
            }
            catch (IOException e)
            {
                LastProblem = e.Message;
            }
            return new DeviceState(stages, active, rate, battery, charging);
        }
    }

    RazerReport? Read(Commands.Command command)
    {
        var answer = transport.Send(Commands.Request(transactionId, command));
        return answer.Status == ReportStatus.Successful ? answer : null;
    }

    public void Dispose() => transport.Dispose();
}

static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = ControlJsonContext.Default };
}

[JsonSerializable(typeof(LightingSettings))]
[JsonSerializable(typeof(PerformanceSettings))]
[JsonSerializable(typeof(PowerSettings))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;
