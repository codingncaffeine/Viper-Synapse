using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Devices;

/// <summary>One lighting zone: the effects it takes and whether its brightness can be set.</summary>
public sealed record LightingZone(IReadOnlyList<string> Effects, bool Brightness);

/// <summary>What is known about one Razer device model before talking to it.</summary>
public sealed record DeviceModel(
    ushort ProductId,
    string Name,
    string Type,
    IReadOnlyDictionary<string, LightingZone> Zones,
    IReadOnlySet<string> Features,
    int MatrixRows,
    int MatrixColumns,
    int DpiMax,
    IReadOnlyList<int> PollRates,
    string Source)
{
    public bool Has(string feature) => Features.Contains(feature);
}

/// <summary>
/// The device models Viper Synapse knows: data/devices.json (imported from OpenRazer) overlaid by
/// data/devices-extra.json (our own entries), both built into this assembly.
/// </summary>
public sealed class DeviceCatalogue
{
    readonly FrozenDictionary<ushort, DeviceModel> _models;

    DeviceCatalogue(Dictionary<ushort, DeviceModel> models) => _models = models.ToFrozenDictionary();

    public static DeviceCatalogue Default { get; } = LoadEmbedded();

    public IReadOnlyCollection<DeviceModel> Models => _models.Values;

    public DeviceModel? Find(ushort productId) => _models.GetValueOrDefault(productId);

    static DeviceCatalogue LoadEmbedded()
    {
        var models = new Dictionary<ushort, DeviceModel>();
        foreach (var name in new[] { "data/devices.json", "data/devices-extra.json" })
        {
            using var stream = typeof(DeviceCatalogue).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"{name} is not built into {typeof(DeviceCatalogue).Assembly.GetName().Name}.");
            foreach (var model in Parse(stream))
                models[model.ProductId] = model;
        }
        return new DeviceCatalogue(models);
    }

    /// <summary>Reads one catalogue file. Later files replace earlier entries with the same product id.</summary>
    public static IEnumerable<DeviceModel> Parse(Stream json)
    {
        var file = JsonSerializer.Deserialize(json, CatalogueJson.Default.CatalogueFile)
            ?? throw new InvalidDataException("Empty device catalogue.");
        foreach (var d in file.Devices)
        {
            var zones = (d.Zones ?? []).ToFrozenDictionary(z => z.Key, z => new LightingZone(z.Value.Effects ?? [], z.Value.Brightness));
            yield return new DeviceModel(
                ushort.Parse(d.Pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                d.Name,
                d.Type,
                zones,
                (d.Features ?? []).ToFrozenSet(),
                d.Matrix is [var rows, var columns] ? rows : 0,
                d.Matrix is [_, var cols] ? cols : 0,
                d.DpiMax,
                d.PollRates ?? [],
                d.Source ?? "");
        }
    }

    internal sealed record CatalogueFile(List<DeviceJson> Devices);

    internal sealed record ZoneJson(List<string>? Effects, bool Brightness);

    internal sealed record DeviceJson(
        string Pid,
        string Name,
        string Type,
        Dictionary<string, ZoneJson>? Zones,
        List<string>? Features,
        List<int>? Matrix,
        [property: JsonPropertyName("dpi_max")] int DpiMax,
        [property: JsonPropertyName("poll_rates")] List<int>? PollRates,
        string? Source);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceCatalogue.CatalogueFile))]
internal sealed partial class CatalogueJson : JsonSerializerContext;
