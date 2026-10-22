using Avalonia.Media.Imaging;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.Services;

/// <summary>
/// Device photos. Viper Synapse ships none of Razer's pictures: the catalogue holds the address of each
/// model's photo on Razer's own servers, and the first time a device is shown its photo is fetched from there
/// into $XDG_CACHE_HOME/viper-synapse/images. Everything happens on pool threads; a photo that cannot be had
/// (offline, moved) is simply absent and the drawn device stands in.
/// </summary>
public static class DeviceImages
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    static string CacheDir()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cache, "viper-synapse", "images");
    }

    /// <summary>A file that will not decode (cut short, not a picture) is thrown away so the next start fetches it again.
    /// The decoder throws several kinds of exception for bad data, hence the wide catch at this one boundary.</summary>
    static Bitmap? Decode(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return new Bitmap(stream);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            File.Delete(file);
            return null;
        }
    }

    public static Task<Bitmap?> LoadAsync(DeviceModel? model) => model?.Image is not { Length: > 0 } url
        ? Task.FromResult<Bitmap?>(null)
        : Task.Run(() => LoadAsync(model.ProductId, url));

    static async Task<Bitmap?> LoadAsync(ushort productId, string url)
    {
        var file = Path.Combine(CacheDir(), $"{productId:x4}{Path.GetExtension(new Uri(url).AbsolutePath)}");
        try
        {
            if (!File.Exists(file))
            {
                await OneAtATime.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!File.Exists(file))
                    {
                        var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        var temp = file + ".tmp";
                        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                        File.Move(temp, file, overwrite: true);
                    }
                }
                finally
                {
                    OneAtATime.Release();
                }
            }
            return Decode(file);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
