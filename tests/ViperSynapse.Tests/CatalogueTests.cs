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
