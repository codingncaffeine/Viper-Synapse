using System.Collections.Concurrent;
using System.Diagnostics;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Service;

/// <summary>
/// The effects a device's firmware does not run, drawn by the service on a thread of its own and sent to the device's
/// live slot only (nothing is written to its storage per frame). A keypad with a per-key grid gets the whole grid each
/// frame (30 a second); other devices get a static colour per lighting zone (20 a second: the Basilisk V4 Pro takes
/// about 25 over its wireless dongle). Key presses reach Reactive and Ripple from the remap session's reader threads.
/// </summary>
public sealed class SoftwareLighting(RazerTransport transport, byte transactionId, DeviceModel? model) : IDisposable
{
    /// <summary>Zones laid out front to back for effects that travel along them.</summary>
    static readonly string[] ZoneOrder = ["scroll", "right", "left", "main", "backlight", "underglow", "logo", "charging"];

    sealed record Group(Animation Animation, IReadOnlyList<byte> Leds, bool Frame);

    readonly ConcurrentQueue<ushort> _presses = new();
    readonly Lock _gate = new();
    Thread? _thread;
    volatile bool _stopping;
    volatile bool _paused;
    IReadOnlyList<Group> _groups = [];

    /// <summary>The CPU temperature for the Temperature effect.</summary>
    static readonly CpuTemperature Temperature = new();

    /// <summary>Dark (the lighting switched off, the screen off, idle): no frames are sent.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>The service draws the zone's effect: the firmware does not run it (or not in that colour mode).</summary>
    public bool Draws(string zone, ZoneEffect effect) =>
        effect.Kind is not (EffectKind.Custom or EffectKind.Off)
        && !EffectSupport.RunsOnDevice(effect, model?.Zones.TryGetValue(zone, out var z) == true ? z.Effects : []);

    /// <summary>Draws these zones' effects from now on, replacing what was drawn; none stops the thread.</summary>
    public void Show(IReadOnlyDictionary<string, ZoneEffect> zones, string deviceType)
    {
        Stop();
        if (zones.Count == 0)
            return;
        var groups = new List<Group>();
        bool grid = model is { MatrixRows: > 0, MatrixColumns: > 0 } && model.Has("custom_frame") && model.Keys.Any(k => k.Leds.Count > 0)
            && model.Zones.Count <= 1;
        if (grid)
        {
            var (_, effect) = zones.First();
            groups.Add(new Group(new Animation(effect, EffectSupport.Grid(model!.MatrixRows, model.MatrixColumns), temperature: Temperature.Read), [], Frame: true));
        }
        else
        {
            // Zones showing the same effect move as one (a wave runs across them); others each on their own.
            foreach (var same in zones.GroupBy(z => z.Value))
            {
                var leds = same.Select(z => z.Key).OrderBy(z => Array.IndexOf(ZoneOrder, z) is var i and >= 0 ? i : ZoneOrder.Length)
                    .Select(z => DeviceCommands.LedId(z, deviceType)).ToList();
                groups.Add(new Group(new Animation(same.Key, EffectSupport.Line(leds.Count), temperature: Temperature.Read), leds, Frame: false));
            }
        }
        lock (_gate)
        {
            _groups = groups;
            _stopping = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "lighting " + transport.Path };
            _thread.Start();
        }
    }

    /// <summary>Stops drawing; the device keeps the last frame until something else is sent.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _stopping = true;
        }
        thread?.Join();
        _presses.Clear();
    }

    /// <summary>A key went down (its source code, as the remap engine knows it). Any thread.</summary>
    public void Press(ushort source)
    {
        if (_thread != null && _presses.Count < 64)
            _presses.Enqueue(source);
    }

    void Run()
    {
        var groups = _groups;
        bool frame = groups.Any(g => g.Frame);
        var period = TimeSpan.FromSeconds(frame ? 1 / 30.0 : 1 / 20.0);
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        bool switched = false;
        while (!_stopping)
        {
            var now = clock.Elapsed;
            double dt = (now - last).TotalSeconds;
            last = now;
            while (_presses.TryDequeue(out var source))
                foreach (var group in groups)
                    Press(group, source);
            foreach (var group in groups)
                group.Animation.Step(dt);
            if (!_paused)
            {
                try
                {
                    foreach (var group in groups)
                        Send(group, ref switched);
                }
                catch (IOException)
                {
                    // Unplugged: the device manager disposes this control when it notices.
                    return;
                }
            }
            var wait = period - (clock.Elapsed - now);
            if (wait > TimeSpan.Zero)
                Thread.Sleep(wait);
        }
    }

    void Press(Group group, ushort source)
    {
        if (!group.Frame)
        {
            // A mouse button: the whole device reacts, a ripple starts from its middle.
            group.Animation.PressAll();
            return;
        }
        foreach (var key in model!.Keys.Where(k => k.Source == source))
            foreach (var (row, column) in key.Leds)
                group.Animation.Press(row * model.MatrixColumns + column);
    }

    void Send(Group group, ref bool switched)
    {
        var colors = group.Animation.Colors;
        if (group.Frame)
        {
            int columns = model!.MatrixColumns;
            // A busy answer drops a row for one frame; the next frame sends it again.
            for (int r = 0; r < model.MatrixRows; r++)
                transport.Send(DeviceCommands.CustomFrameRow(transactionId, r, 0, columns - 1, colors.Skip(r * columns).Take(columns).ToList()));
            if (!switched)
            {
                transport.Send(DeviceCommands.CustomFrameEffect(transactionId));
                switched = true;
            }
            return;
        }
        for (int i = 0; i < group.Leds.Count; i++)
            transport.Send(DeviceCommands.Effect(transactionId, group.Leds[i], new ZoneEffect(EffectKind.Static, colors[i], default), DeviceCommands.NoStore));
    }

    public void Dispose() => Stop();
}
