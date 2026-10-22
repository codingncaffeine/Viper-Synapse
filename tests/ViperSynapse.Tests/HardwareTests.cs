using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

/// <summary>
/// Checks against a real plugged-in Tartarus V2 Pro. Opt-in only (VIPER_HARDWARE_TESTS=1): they change the device
/// and put it back. Never part of the normal run or of CI.
/// </summary>
public class HardwareTests
{
    static bool Enabled => Environment.GetEnvironmentVariable("VIPER_HARDWARE_TESTS") == "1";

    static HidrawNode? Tartarus() => HidrawNode.Enumerate()
        .FirstOrDefault(n => n.VendorId == 0x1532 && n.ProductId == 0x02EB && RazerProbe.HasControlReport(n));

    [Fact]
    public void Switch_off_lighting_darkens_the_keypad_and_brings_its_brightness_back()
    {
        Assert.SkipUnless(Enabled, "hardware tests are opt-in (VIPER_HARDWARE_TESTS=1)");
        var node = Tartarus();
        Assert.SkipWhen(node == null, "no Tartarus V2 Pro plugged in");
        using var transport = new RazerTransport(HidrawDevice.Open(node!.DevicePath));
        var identity = RazerProbe.Identify(transport)!;
        int Brightness() => transport.Send(RazerReport.Create(identity.TransactionId, 0x0F, 0x84, 0x01, 0x05, 0x00)).Arguments[2];
        int before = Brightness();

        var control = new DeviceControl(transport, identity.TransactionId, DeviceCatalogue.Default.Find(0x02EB));
        var lighting = new LightingSettings(new Dictionary<string, ZoneLighting> { ["main"] = new(ZoneEffect.Default, 100) }, OffWhenIdleMinutes: 5);
        control.Apply(Profile.Create("test") with { Lighting = lighting });
        Assert.Equal(0xFF, Brightness());
        control.SetDimmed(true);
        Assert.Equal(0, Brightness());
        control.SetDimmed(false);
        Assert.Equal(0xFF, Brightness());

        // Put the keypad back as it was.
        transport.Send(DeviceCommands.Brightness(identity.TransactionId, 0x05, (int)Math.Round(before / 2.55)));
    }
}
