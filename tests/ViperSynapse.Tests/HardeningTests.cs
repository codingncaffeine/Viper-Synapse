using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Tests;

/// <summary>What the service and app accept from files, the bus and the network is bounded.</summary>
public class HardeningTests
{
    [Theory]
    [InlineData("https://assets.razerzone.com/eeimages/products/x/razer-naga.png", true)]
    [InlineData("https://medias-p1.phoenix.razer.com/sys-master-phoenix-images-container/a/b/basilisk-v4-pro.png", true)]
    [InlineData("https://rzrwarranty.s3.amazonaws.com/abc.png", true)]
    [InlineData("http://assets.razerzone.com/x.png", false)]          // not HTTPS
    [InlineData("https://hwimg.nl/x.png", false)]                     // not Razer's
    [InlineData("https://razer.com.example.net/x.png", false)]        // Razer's name as a prefix only
    [InlineData("https://notrazer.com/x.png", false)]                 // Razer's name as a suffix of another word
    [InlineData("https://assets.razerzone.com/payload.exe", false)]   // not a picture
    [InlineData("file:///etc/passwd", false)]
    public void Photos_come_only_from_razers_hosts_over_https(string url, bool allowed) =>
        Assert.Equal(allowed, DeviceImages.Allowed(url));

    [Fact]
    public void A_stage_table_never_claims_more_stages_than_it_holds_or_a_dpi_out_of_range()
    {
        var stages = Enumerable.Range(0, 12).Select(_ => new DpiStage(1_000_000, -5)).ToList();
        var report = DeviceCommands.DpiStages(0x1F, new PerformanceSettings(stages, 11, 1000));
        var a = report.Arguments;
        Assert.Equal(PerformanceSettings.MaxStages, a[2]);
        Assert.Equal(PerformanceSettings.MaxStages, a[1]);
        Assert.Equal((0xFF, 0xFF), (a[4], a[5]));     // x clamped to 65535
        Assert.Equal((0x00, 0x64), (a[6], a[7]));     // y clamped to 100
    }

    sealed class Host : IInputSink, IEngineHost
    {
        public int Wheel;
        public int Keys;
        public void Key(ushort code, int value) => Keys++;
        public void Relative(ushort axis, int value) { if (axis == RelativeAxes.Wheel) Wheel += value; }
        public void Sync() { }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) => throw error;
    }

    [Fact]
    public async Task A_macro_file_cannot_ask_for_an_endless_loop_a_huge_scroll_or_an_impossible_delay()
    {
        var host = new Host();
        var runner = new MacroRunner(host, host, TimeProvider.System);
        await runner.PlayAsync(
        [
            new ScrollStep(int.MaxValue),
            new RandomDelayStep(int.MinValue, -5),                // clamped to no delay
            new DelayStep(-1),                                   // -1 ms is "forever" to Task.Delay
            new LoopStartStep(int.MaxValue), new KeyStep(30, true), new KeyStep(30, false), new LoopEndStep(),
        ], CancellationToken.None);
        Assert.Equal(MacroRunner.MaxScrollClicks, host.Wheel);
        Assert.Equal(2 * MacroRunner.MaxLoops, host.Keys);
    }
}

public class StageTableTests
{
    static readonly PerformanceSettings Five = new(
        [new(400, 400), new(800, 800), new(1600, 1600), new(3200, 3200), new(6400, 6400)], 2, 1000);

    [Fact]
    public void The_basilisk_v4_pro_table_is_written_byte_for_byte_as_the_mouse_keeps_it()
    {
        // 04/86 as read from the Basilisk V4 Pro on 2026-10-22: each stage is id, x, y and x once more.
        byte[] read = Convert.FromHexString("010305" + "0101900190" + "0190" + "0203200320" + "0320" + "0306400640" + "0640"
                                            + "040C800C80" + "0C80" + "0519001900" + "1900" + "00");
        var written = DeviceCommands.DpiStages(0x1F, Five, thirdValue: true).Arguments.ToArray()[..read.Length];
        Assert.Equal(read, written);
    }

    [Fact]
    public void The_naga_table_keeps_zeros_after_each_stage()
    {
        // 04/86 from the Naga Left-Handed 2020: id, x, y, then 00 00.
        byte[] read = Convert.FromHexString("010305" + "01019001900000" + "02032003200000" + "03064006400000" + "040C800C800000" + "05190019000000");
        var written = DeviceCommands.DpiStages(0x1F, Five).Arguments.ToArray()[..read.Length];
        Assert.Equal(read, written);
    }
}
