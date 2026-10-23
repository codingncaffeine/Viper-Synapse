using ViperSynapse.Core.Devices;

namespace ViperSynapse.Tests;

public class CatalogueTests
{
    [Fact]
    public void The_catalogue_covers_the_major_device_kinds()
    {
        var types = DeviceCatalogue.Default.Models.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(types["mouse"] > 100);
        Assert.True(types["keyboard"] > 100);
        Assert.True(types["keypad"] >= 7);
    }

    [Fact]
    public void The_naga_left_handed_has_its_three_zones_and_dpi()
    {
        var naga = DeviceCatalogue.Default.Find(0x008D);
        Assert.NotNull(naga);
        Assert.Equal("mouse", naga.Type);
        Assert.Equal(20000, naga.DpiMax);
        Assert.Equal(["logo", "right", "scroll"], naga.Zones.Keys.Order());
        Assert.Contains("static", naga.Zones["logo"].Effects);
        Assert.True(naga.Has("dpi"));
        Assert.Equal((1, 3), (naga.MatrixRows, naga.MatrixColumns));
    }

    [Fact]
    public void Our_own_entries_cover_devices_openrazer_lacks()
    {
        var tartarus = DeviceCatalogue.Default.Find(0x02EB);
        Assert.NotNull(tartarus);
        Assert.Equal("keypad", tartarus.Type);
        Assert.Equal("Razer Tartarus V2 Pro", tartarus.Name);
        // The manual numbers 27 keys; the key map read from the device gives their ids.
        Assert.Equal(27, tartarus.Keys.Count(k => k.Group == "main"));
        var w = tartarus.Keys.Single(k => k.Number == 9);
        Assert.Equal(("W", (byte)0x12), (w.Label, w.HardwareId));
        Assert.Equal("KEY_W", Core.Input.KeyCodes.Name(w.Code));
        Assert.Equal(0x3b, tartarus.Keys.Single(k => k.Label == "Hypershift").HardwareId);
    }

    [Fact]
    public void The_basilisk_v4_pro_has_its_probed_zones_rates_and_scroll_wheel()
    {
        var basilisk = DeviceCatalogue.Default.Find(0x00E3);
        Assert.NotNull(basilisk);
        Assert.Equal("mouse", basilisk.Type);
        Assert.Equal(50000, basilisk.DpiMax);
        Assert.Equal(["logo", "scroll", "underglow"], basilisk.Zones.Keys.Order());
        // The LED ids the probe answered on: 01 scroll, 04 logo, 0A underglow.
        Assert.Equal([0x04, 0x01, 0x0A], new[] { "logo", "scroll", "underglow" }.Select(z => (int)Core.Protocol.DeviceCommands.LedId(z, "mouse")));
        Assert.Equal(8000, basilisk.PollRates.Max());
        Assert.True(basilisk.Has("scroll_wheel") && basilisk.Has("battery") && basilisk.Has("idle_time"));
        // Nine buttons Linux sees, five the firmware keeps (DPI, profile, scroll mode, clutch, M1).
        Assert.Equal(9, basilisk.Keys.Count(k => k.Source != 0));
        Assert.Equal(14, basilisk.Keys.Count);
    }

    [Fact]
    public void An_extra_entry_replaces_the_imported_one()
    {
        static Stream Json(string name) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"devices":[{"pid":"0001","name":"{{name}}","type":"mouse"}]}"""));
        var merged = new Dictionary<ushort, DeviceModel>();
        foreach (var m in DeviceCatalogue.Parse(Json("imported")).Concat(DeviceCatalogue.Parse(Json("ours"))))
            merged[m.ProductId] = m;
        Assert.Equal("ours", merged[1].Name);
    }
}
