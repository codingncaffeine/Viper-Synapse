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
