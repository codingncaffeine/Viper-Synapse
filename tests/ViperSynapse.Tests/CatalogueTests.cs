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
        // Nine buttons Linux sees, five the firmware keeps (sensitivity, profile on the M1 edge, scroll mode, the two
        // front side buttons): those are bindable through their relay keys, F13 to F17 (HID 0x68 to 0x6C).
        Assert.Equal(14, basilisk.Keys.Count(k => k.Source != 0));
        Assert.Equal(14, basilisk.Keys.Count);
        Assert.Equal([0x68, 0x69, 0x6A, 0x6B, 0x6C], basilisk.Keys.Where(k => k.RelayUsage != 0).Select(k => (int)k.RelayUsage).Order());
        Assert.All(basilisk.Keys.Where(k => k.RelayUsage != 0), k => Assert.NotEqual(0, k.HardwareId));
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

public class RelayAndOnboardTests
{
    [Fact]
    public void Relay_reports_raise_one_change_per_press_and_release()
    {
        var seen = new List<(byte, bool)>();
        // (no device: Handle is fed reports directly)
        var relay = (Hardware.RelayButtons)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Hardware.RelayButtons));
        typeof(Hardware.RelayButtons).GetField("_watched", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(relay, new HashSet<byte> { 0x68, 0x69 });
        typeof(Hardware.RelayButtons).GetField("_down", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(relay, new HashSet<byte>());
        relay.Changed += (u, d) => seen.Add((u, d));
        relay.Handle([0, 0, 0x68, 0, 0, 0, 0, 0]);
        relay.Handle([0, 0, 0x68, 0x59, 0, 0, 0, 0]);   // (Keypad 1 is not watched)
        relay.Handle([0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal([((byte)0x68, true), ((byte)0x68, false)], seen);
    }

    [Fact]
    public void Onboard_functions_read_as_synapse_names_them()
    {
        static Core.Protocol.RazerReport Answer(params byte[] args) => Core.Protocol.RazerReport.Create(0x1F, 0x02, 0x8C, args);
        Assert.Equal("Keypad 2", Core.Protocol.OnboardButtons.Describe(Answer(0x01, 0x39, 0x00, 0x02, 0x02, 0x00, 0x5A), 0x39));
        Assert.Equal("Cycle Up Sensitivity Stages", Core.Protocol.OnboardButtons.Describe(Answer(0x01, 0x60, 0x00, 0x06, 0x01, 0x06), 0x60));
        Assert.Equal("Cycle Up Profile", Core.Protocol.OnboardButtons.Describe(Answer(0x01, 0x6A, 0x00, 0x12, 0x01, 0x01), 0x6A));
        Assert.Equal("Repeat Scroll Left", Core.Protocol.OnboardButtons.Describe(Answer(0x01, 0x34, 0x00, 0x0E, 0x01, 0x68, 0x00, 0x14), 0x34));
        Assert.Null(Core.Protocol.OnboardButtons.Describe(Answer(0x01, 0x39, 0x00, 0x02, 0x02, 0x00, 0x5A), 0x80));   // (a stale answer)
    }
}

public class Perfect180Tests
{
    [Theory]
    [InlineData(2000, 10)]
    [InlineData(-2000, 7)]
    [InlineData(4091, 1)]
    [InlineData(13, 40)]
    public void The_steps_add_up_to_the_whole_turn(int total, int steps)
    {
        var parts = Core.Bindings.Perfect180.Steps(total, steps).ToList();
        Assert.Equal(Math.Max(1, steps), parts.Count);
        Assert.Equal(total, parts.Sum());
    }

    [Fact]
    public void Known_games_turn_half_way_round()
    {
        // CS2 at sensitivity 2: 180 / (0.022 x 2) = 4090.9; Valorant at 1: 180 / 0.07 = 2571.4
        Assert.Equal(4091, Core.Bindings.Perfect180.CountsFor(0.022, 2));
        Assert.Equal(2571, Core.Bindings.Perfect180.CountsFor(0.07, 1));
        Assert.Equal(0, Core.Bindings.Perfect180.CountsFor(0.022, 0));
    }

    [Fact]
    public void A_perfect_180_binding_survives_a_round_trip_through_the_settings_file()
    {
        Core.Bindings.Binding b = new Core.Bindings.Perfect180Binding(4091, true, 15, "Counter-Strike 2 / CS:GO", 2);
        var json = System.Text.Json.JsonSerializer.Serialize(b);
        Assert.Contains("perfect-180", json);
        Assert.Equal(b, System.Text.Json.JsonSerializer.Deserialize<Core.Bindings.Binding>(json));
    }
}

public class KeyNameTests
{
    [Fact]
    public void Numpad_keys_read_as_numpad()
    {
        Core.Input.KeyCodes.TryParse("KEY_KP3", out var kp3);
        Core.Input.KeyCodes.TryParse("KEY_KPPLUS", out var plus);
        Assert.Equal("Numpad 3", Core.Bindings.BindingText.KeyLabel(kp3));
        Assert.Equal("Numpad +", Core.Bindings.BindingText.KeyLabel(plus));
    }
}
