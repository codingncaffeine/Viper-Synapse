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

    /// <summary>A product photo is well under a megabyte; anything past this is not one.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>Razer's own sites and the two storage hosts Razer serves product pictures from. A catalogue address
    /// anywhere else is not fetched: the photo is a nicety, not worth trusting an arbitrary server for.</summary>
    static readonly string[] Hosts = ["razer.com", "razerzone.com", "hybrismediaprod.blob.core.windows.net", "rzrwarranty.s3.amazonaws.com"];

    static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>Whether a catalogue address may be fetched: HTTPS, on one of <see cref="Hosts"/>, a picture by its name.</summary>
    public static bool Allowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
        && Extensions.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());
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

    /// <summary>Fetches a picture of at most <see cref="MaxBytes"/>, refusing anything the server does not call an image
    /// (an error page, say) and stopping the moment a body runs past the limit, whatever its header claimed.</summary>
    static async Task<byte[]?> DownloadAsync(string url)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            || response.Content.Headers.ContentLength > MaxBytes)
            return null;
        await using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + n > MaxBytes)
                return null;
            copy.Write(buffer, 0, n);
        }
        return copy.ToArray();
    }

    public static Task<Bitmap?> LoadAsync(DeviceModel? model) => model?.Image is not { Length: > 0 } url || !Allowed(url)
        ? Task.FromResult<Bitmap?>(null)
        : Task.Run(() => LoadAsync(model.ProductId, url));

    static async Task<Bitmap?> LoadAsync(ushort productId, string url)
    {
        // The file name is ours (product id + a checked extension), never taken from the server.
        var file = Path.Combine(CacheDir(), $"{productId:x4}{Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant()}");
        try
        {
            if (!File.Exists(file))
            {
                await OneAtATime.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!File.Exists(file))
                    {
                        var bytes = await DownloadAsync(url).ConfigureAwait(false);
                        if (bytes == null)
                            return null;
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
