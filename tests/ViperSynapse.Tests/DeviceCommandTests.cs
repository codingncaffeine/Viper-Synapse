using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Tests;

/// <summary>Each command against the bytes the OpenRazer driver sends for it (razerchromacommon.c).</summary>
public class DeviceCommandTests
{
    static string Hex(RazerReport r) =>
        $"{r.CommandClass:x2} {r.CommandId:x2} [{r.DataSize:x2}] " + string.Join(' ', r.Arguments[..r.DataSize].ToArray().Select(b => b.ToString("x2")));

    static readonly Rgb Red = new(0xFF, 0, 0), Blue = new(0, 0, 0xFF);

    [Theory]
    [InlineData(EffectKind.Off, ColorMode.Single, "0f 02 [06] 01 05 00 00 00 00")]
    [InlineData(EffectKind.Static, ColorMode.Single, "0f 02 [09] 01 05 01 00 00 01 ff 00 00")]
    [InlineData(EffectKind.Breathing, ColorMode.Single, "0f 02 [09] 01 05 02 01 00 01 ff 00 00")]
    [InlineData(EffectKind.Breathing, ColorMode.Dual, "0f 02 [0c] 01 05 02 02 00 02 ff 00 00 00 00 ff")]
    [InlineData(EffectKind.Breathing, ColorMode.Random, "0f 02 [06] 01 05 02 00 00 00")]
    [InlineData(EffectKind.Spectrum, ColorMode.Single, "0f 02 [06] 01 05 03 00 00 00")]
    [InlineData(EffectKind.Wave, ColorMode.Single, "0f 02 [06] 01 05 04 01 28 00")]
    [InlineData(EffectKind.Reactive, ColorMode.Single, "0f 02 [09] 01 05 05 00 02 01 ff 00 00")]
    [InlineData(EffectKind.Starlight, ColorMode.Dual, "0f 02 [0c] 01 05 07 00 02 02 ff 00 00 00 00 ff")]
    [InlineData(EffectKind.Starlight, ColorMode.Random, "0f 02 [06] 01 05 07 00 02 00")]
    [InlineData(EffectKind.Wheel, ColorMode.Single, "0f 02 [06] 01 05 0a 01 28 00")]
    public void Effects_match_the_driver(EffectKind kind, ColorMode colors, string expected)
    {
        var report = DeviceCommands.Effect(0x1F, 0x05, new ZoneEffect(kind, Red, Blue, colors, Speed: 2));
        Assert.Equal(expected, Hex(report));
        Assert.True(report.ChecksumValid);
    }

    [Fact]
    public void A_custom_frame_is_a_report_per_row_then_the_custom_effect()
    {
        var frame = CustomFrame.Filled(5, 6, new Rgb(0, 0x40, 0)).With([(0, 1)], new Rgb(0xFF, 0, 0));
        var rows = DeviceCommands.CustomFrame(0x1F, frame).ToList();
        Assert.Equal(5, rows.Count);
        // Row 0: row, start, stop, then RGB per LED; data size 0x47 as the driver sends it.
        Assert.StartsWith("0f 03 [47] 00 00 00 00 05 00 40 00 ff 00 00 00 40 00", Hex(rows[0]));
        Assert.StartsWith("0f 03 [47] 00 00 04 00 05 00 40 00", Hex(rows[4]));
        Assert.Equal("0f 02 [0c] 00 00 08 00 00 00 00 00 00 00 00 00", Hex(DeviceCommands.Effect(0x1F, 0x05, ZoneEffect.Default with { Kind = EffectKind.Custom })));
    }

    [Fact]
    public void Lighting_for_the_live_slot_differs_only_in_the_store_byte()
    {
        // The wave the Tartarus V2 Pro took into its live slot (it then left the per-key frame), 2026-10-25.
        var wave = new ZoneEffect(EffectKind.Wave, Red, Blue, ColorMode.Single, Speed: 2);
        Assert.Equal("0f 02 [06] 00 05 04 01 28 00", Hex(DeviceCommands.Effect(0x1F, 0x05, wave, DeviceCommands.NoStore)));
        Assert.Equal("0f 04 [03] 00 05 ff", Hex(DeviceCommands.Brightness(0x1F, 0x05, 100, DeviceCommands.NoStore)));
    }

    [Fact]
    public void Brightness_is_a_percentage_of_255()
    {
        Assert.Equal("0f 04 [03] 01 04 ff", Hex(DeviceCommands.Brightness(0x1F, 0x04, 100)));
        Assert.Equal("0f 04 [03] 01 04 80", Hex(DeviceCommands.Brightness(0x1F, 0x04, 50)));
    }

    [Fact]
    public void Dpi_and_stages_round_trip_through_the_stage_table()
    {
        Assert.Equal("04 05 [07] 01 19 00 0c 80 00 00", Hex(DeviceCommands.Dpi(0x1F, 6400, 3200)));

        var settings = new PerformanceSettings([new(400, 400), new(800, 800), new(1600, 1600), new(3200, 3200), new(6400, 6400)], 4, 1000);
        var report = DeviceCommands.DpiStages(0x1F, settings);
        // The Naga's own answer to a stage read, captured 2026-10-22, carries this same table.
        Assert.Equal("04 06 [26] 01 05 05 01 01 90 01 90 00 00 02 03 20 03 20 00 00 03 06 40 06 40 00 00 04 0c 80 0c 80 00 00 05 19 00 19 00 00 00",
            Hex(report));
        var back = DeviceCommands.ReadStages(report, 1000);
        Assert.Equal(settings.Stages, back.Stages);
        Assert.Equal(4, back.ActiveStage);
    }

    [Fact]
    public void Polling_rates_use_the_device_codes()
    {
        Assert.Equal("00 05 [01] 01", Hex(DeviceCommands.PollingRate(0x1F, 1000)));
        Assert.Equal("00 05 [01] 08", Hex(DeviceCommands.PollingRate(0x1F, 125)));
        Assert.Equal("00 40 [02] 00 01", Hex(DeviceCommands.HyperPollingRate(0x1F, 8000)));
        Assert.Equal("00 40 [02] 00 08", Hex(DeviceCommands.HyperPollingRate(0x1F, 1000)));
    }

    [Fact]
    public void Power_settings_are_clamped_to_what_the_device_takes()
    {
        Assert.Equal("07 03 [02] 01 2c", Hex(DeviceCommands.IdleTime(0x1F, 300)));
        Assert.Equal("07 03 [02] 00 3c", Hex(DeviceCommands.IdleTime(0x1F, 5)));
        Assert.Equal("07 01 [01] 26", Hex(DeviceCommands.LowBatteryThreshold(0x1F, 15)));
    }
}
