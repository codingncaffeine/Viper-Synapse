using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Core.Protocol;

/// <summary>
/// The "set" commands Viper Synapse sends, built byte for byte as the OpenRazer driver builds them for the same
/// devices: extended-matrix lighting (class 0x0F), sensitivity (class 0x04), polling rate (class 0x00) and power
/// (class 0x07). Every one stores to the device's variable storage, as Synapse and OpenRazer do; lighting can also go
/// to the live slot (<see cref="NoStore"/>), which is what a keypad shows. Device mode
/// (0x00/0x04) is deliberately absent: some keypads reset on it.
/// </summary>
public static class DeviceCommands
{
    public const byte VarStore = 0x01;

    /// <summary>The live slot: shown, not kept. The per-key frame runs here.</summary>
    public const byte NoStore = 0x00;

    /// <summary>LED ids by zone name, as the catalogue names zones. "main" is the backlight on keyboards and
    /// keypads and the whole device on mice.</summary>
    public static byte LedId(string zone, string deviceType) => zone switch
    {
        "main" => deviceType is "keyboard" or "keypad" ? (byte)0x05 : (byte)0x00,
        "backlight" => 0x05,
        "scroll" => 0x01,
        "logo" => 0x04,
        "right" => 0x10,
        "left" => 0x11,
        "charging" => 0x20,
        "underglow" => 0x0A,
        _ => 0x00,
    };

    public static RazerReport Effect(byte tid, byte led, ZoneEffect e, byte store = VarStore)
    {
        var a = new byte[12];
        a[0] = store;
        a[1] = led;
        byte size;
        switch (e.Kind)
        {
            case EffectKind.Off:
                size = 0x06;
                break;
            case EffectKind.Static:
                a[2] = 0x01;
                a[5] = 0x01;
                Put(a, 6, e.Color1);
                size = 0x09;
                break;
            case EffectKind.Breathing when e.Colors == ColorMode.Random:
                a[2] = 0x02;
                size = 0x06;
                break;
            case EffectKind.Breathing when e.Colors == ColorMode.Dual:
                a[2] = 0x02;
                a[3] = 0x02;
                a[5] = 0x02;
                Put(a, 6, e.Color1);
                Put(a, 9, e.Color2);
                size = 0x0C;
                break;
            case EffectKind.Breathing:
                a[2] = 0x02;
                a[3] = 0x01;
                a[5] = 0x01;
                Put(a, 6, e.Color1);
                size = 0x09;
                break;
            case EffectKind.Spectrum:
                a[2] = 0x03;
                size = 0x06;
                break;
            case EffectKind.Wave:
                a[2] = 0x04;
                a[3] = e.Reverse ? (byte)0x02 : (byte)0x01;
                a[4] = 0x28;
                size = 0x06;
                break;
            case EffectKind.Reactive:
                a[2] = 0x05;
                a[4] = (byte)Math.Clamp(e.Speed, 1, 4);
                a[5] = 0x01;
                Put(a, 6, e.Color1);
                size = 0x09;
                break;
            case EffectKind.Starlight:
                a[2] = 0x07;
                a[4] = (byte)Math.Clamp(e.Speed, 1, 3);
                if (e.Colors == ColorMode.Random)
                {
                    size = 0x06;
                }
                else if (e.Colors == ColorMode.Dual)
                {
                    a[5] = 0x02;
                    Put(a, 6, e.Color1);
                    Put(a, 9, e.Color2);
                    size = 0x0C;
                }
                else
                {
                    a[5] = 0x01;
                    Put(a, 6, e.Color1);
                    size = 0x09;
                }
                break;
            case EffectKind.Custom:
                // The frame itself goes first, row by row (CustomFrameRow); this switches the device over to it.
                return CustomFrameEffect(tid);
            case EffectKind.Wheel:
                a[2] = 0x0A;
                a[3] = e.Reverse ? (byte)0x02 : (byte)0x01;
                a[4] = 0x28;
                size = 0x06;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.Kind, "Unknown effect.");
        }
        return RazerReport.Create(tid, 0x0F, 0x02, a.AsSpan(0, size), size);
    }

    /// <summary>One row of the per-key frame (0x0F/0x03, data size 0x47 as the driver sends it): row, first and last
    /// column, then an RGB triple per LED.</summary>
    public static RazerReport CustomFrameRow(byte tid, int row, int start, int stop, IReadOnlyList<Rgb> colors)
    {
        var a = new byte[RazerReport.MaxArguments];
        a[2] = (byte)row;
        a[3] = (byte)start;
        a[4] = (byte)stop;
        for (int i = 0; i <= stop - start && 5 + i * 3 + 2 < a.Length; i++)
            Put(a, 5 + i * 3, colors[i]);
        return RazerReport.Create(tid, 0x0F, 0x03, a, 0x47);
    }

    /// <summary>Shows the frame sent with <see cref="CustomFrameRow"/> (effect 0x08, no storage, as the driver sends it).</summary>
    public static RazerReport CustomFrameEffect(byte tid) =>
        RazerReport.Create(tid, 0x0F, 0x02, new byte[] { 0x00, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0x0C);

    /// <summary>The whole frame, a report per row.</summary>
    public static IEnumerable<RazerReport> CustomFrame(byte tid, CustomFrame frame)
    {
        for (int r = 0; r < frame.Rows; r++)
            yield return CustomFrameRow(tid, r, 0, frame.Columns - 1, frame.Colors.Skip(r * frame.Columns).Take(frame.Columns).ToList());
    }

    /// <summary>Brightness 0-100 % as the device's 0-255.</summary>
    public static RazerReport Brightness(byte tid, byte led, int percent, byte store = VarStore) =>
        RazerReport.Create(tid, 0x0F, 0x04, store, led, (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0, MidpointRounding.AwayFromZero));

    public static RazerReport Dpi(byte tid, int x, int y)
    {
        (x, y) = (ClampDpi(x), ClampDpi(y));
        return RazerReport.Create(tid, 0x04, 0x05, VarStore, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0);
    }

    /// <summary>Sensitivity as the device takes it: two bytes, and no sensor goes below 100.</summary>
    public static int ClampDpi(int dpi) => Math.Clamp(dpi, 100, ushort.MaxValue);

    /// <summary>The stage table: active stage (1-based), count, then seven bytes a stage. Devices that keep a third copy
    /// of each stage's sensitivity in the last two bytes of its entry (the Basilisk V4 Pro reads back 400 400 400) get
    /// it written the same way when <paramref name="thirdValue"/> is set; others (the Naga) keep zeros.</summary>
    public static RazerReport DpiStages(byte tid, PerformanceSettings p, bool thirdValue = false)
    {
        var a = new byte[0x26];
        a[0] = VarStore;
        // Never more stages than the table holds, so the count matches what follows it.
        int count = Math.Min(p.Stages.Count, PerformanceSettings.MaxStages);
        a[1] = (byte)(Math.Clamp(p.ActiveStage, 0, count - 1) + 1);
        a[2] = (byte)count;
        for (int i = 0; i < p.Stages.Count && i < PerformanceSettings.MaxStages; i++)
        {
            int at = 3 + i * 7;
            a[at] = (byte)(i + 1);
            int x = ClampDpi(p.Stages[i].X), y = ClampDpi(p.Stages[i].Y);
            a[at + 1] = (byte)(x >> 8);
            a[at + 2] = (byte)x;
            a[at + 3] = (byte)(y >> 8);
            a[at + 4] = (byte)y;
            if (thirdValue)
            {
                a[at + 5] = (byte)(x >> 8);
                a[at + 6] = (byte)x;
            }
        }
        return RazerReport.Create(tid, 0x04, 0x06, a, 0x26);
    }

    /// <summary>Reads a stage table answer (0x04/0x86) back into settings.</summary>
    public static PerformanceSettings ReadStages(RazerReport answer, int pollingRate)
    {
        var a = answer.Arguments;
        int count = Math.Min((int)a[2], PerformanceSettings.MaxStages);
        var stages = new List<DpiStage>();
        for (int i = 0; i < count; i++)
        {
            int at = 3 + i * 7;
            stages.Add(new DpiStage((a[at + 1] << 8) | a[at + 2], (a[at + 3] << 8) | a[at + 4]));
        }
        return new PerformanceSettings(stages, Math.Max(0, a[1] - 1), pollingRate);
    }

    /// <summary>The classic polling command: 1000, 500 or 125 Hz.</summary>
    public static RazerReport PollingRate(byte tid, int hz) =>
        RazerReport.Create(tid, 0x00, 0x05, hz switch { >= 1000 => (byte)0x01, >= 500 => (byte)0x02, _ => (byte)0x08 });

    /// <summary>The newer polling read (0x00/0xC0) and its answer, as the Tartarus V2 Pro gives it: 00 then the rate code.</summary>
    public static RazerReport GetHyperPollingRate(byte tid) => RazerReport.Create(tid, 0x00, 0xC0, 0x00, 0x00);

    public static int ReadHyperPollingRate(RazerReport answer) => answer.Arguments[1] switch
    {
        0x01 => 8000,
        0x02 => 4000,
        0x04 => 2000,
        0x08 => 1000,
        0x10 => 500,
        0x20 => 250,
        0x40 => 125,
        _ => 0,
    };

    /// <summary>Selects the keypad's keymap 1-8, as its keymap button does (0x02/0x37; read back with 0x02/0xB7).</summary>
    public static RazerReport SelectKeymap(byte tid, int keymap) => RazerReport.Create(tid, 0x02, 0x37, (byte)Math.Clamp(keymap, 1, 8));

    /// <summary>HyperPolling devices (up to 8000 Hz) take the newer command.</summary>
    public static RazerReport HyperPollingRate(byte tid, int hz) =>
        RazerReport.Create(tid, 0x00, 0x40, 0x00, hz switch
        {
            >= 8000 => (byte)0x01,
            >= 4000 => (byte)0x02,
            >= 2000 => (byte)0x04,
            >= 1000 => (byte)0x08,
            >= 500 => (byte)0x10,
            >= 250 => (byte)0x20,
            _ => (byte)0x40,
        });

    public static int ReadPollingRate(RazerReport answer) => answer.Arguments[0] switch
    {
        0x01 => 1000,
        0x02 => 500,
        0x08 => 125,
        _ => 0,
    };

    // Sensor calibration (class 0x0B), as captured from Synapse by others (OpenRazer issue 783, the fang project) and
    // verified on the Naga Left-Handed 2020 by reading back: 0x00 then the sensor id 0x04 lead every command.

    /// <summary>Calibration mode off resets the sensor to its own defaults.</summary>
    public static RazerReport CalibrationMode(byte tid, bool on) => RazerReport.Create(tid, 0x0B, 0x03, 0x00, 0x04, on ? (byte)0x01 : (byte)0x00);

    /// <summary>Smart Tracking at level 0, 1 or 2 (1, 2 or 3 mm lift-off).</summary>
    public static RazerReport SmartTracking(byte tid, int level) =>
        RazerReport.Create(tid, 0x0B, 0x0B, 0x00, 0x04, 0x01, (byte)Math.Clamp(level, 0, 2));

    /// <summary>Tracking mode 0, the sensor's own default, as the Naga Left-Handed 2020 reads before Synapse touches it.</summary>
    public static RazerReport TrackingModeDefault(byte tid) => RazerReport.Create(tid, 0x0B, 0x0B, 0x00, 0x04, 0x00, 0x00);

    /// <summary>The tracking mode read back (0x0B/0x8B): the Smart Tracking level, or -1 for the sensor's default.</summary>
    public static int ReadLiftOffLevel(RazerReport answer) => answer.Arguments[2] == 0x01 ? answer.Arguments[3] : -1;

    public static RazerReport IdleTime(byte tid, int seconds)
    {
        seconds = Math.Clamp(seconds, 60, 900);
        return RazerReport.Create(tid, 0x07, 0x03, (byte)(seconds >> 8), (byte)seconds);
    }

    /// <summary>The low-battery threshold on the device's 0-255 scale, 5% to 100%. OpenRazer stops at 0x3F (25%), but the
    /// Basilisk V4 Pro ships set to 0x4D (30%) and stores any value written.</summary>
    public static RazerReport LowBatteryThreshold(byte tid, int percent) =>
        RazerReport.Create(tid, 0x07, 0x01, (byte)Math.Clamp((int)Math.Round(percent * 2.55), 0x0C, 0xFF));

    /// <summary>Scroll acceleration on or off (0x02/0x16; read back with 0x02/0x96). On or off only: a level byte is
    /// accepted but not kept.</summary>
    public static RazerReport ScrollAcceleration(byte tid, bool on) => RazerReport.Create(tid, 0x02, 0x16, VarStore, on ? (byte)1 : (byte)0);

    /// <summary>Smart-Reel on or off (0x02/0x17; read back with 0x02/0x97).</summary>
    public static RazerReport SmartReel(byte tid, bool on) => RazerReport.Create(tid, 0x02, 0x17, VarStore, on ? (byte)1 : (byte)0);

    public static int ReadBatteryPercent(RazerReport answer) => (int)Math.Round(answer.Arguments[1] / 2.55);

    static void Put(byte[] a, int at, Rgb c)
    {
        a[at] = c.R;
        a[at + 1] = c.G;
        a[at + 2] = c.B;
    }
}
