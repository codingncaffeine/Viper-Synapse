namespace ViperSynapse.Hardware.Linux;

/// <summary>
/// The CPU temperature from the kernel's hwmon sensors: AMD's k10temp (Tctl) or zenpower, Intel's coretemp (the
/// package), or an ARM board's cpu_thermal. Read at most once a second; null when there is no such sensor.
/// </summary>
public sealed class CpuTemperature(string root = "/sys/class/hwmon")
{
    static readonly string[] Drivers = ["k10temp", "zenpower", "coretemp", "cpu_thermal"];

    readonly Lock _gate = new();
    string? _input;
    bool _searched;
    double? _last;
    long _readAt = long.MinValue;

    /// <summary>Degrees Celsius, or null when no CPU sensor was found or it cannot be read.</summary>
    public double? Read()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now - _readAt < 1000)
                return _last;
            _readAt = now;
            if (!_searched)
            {
                _searched = true;
                _input = Find();
            }
            try
            {
                _last = _input != null && int.TryParse(File.ReadAllText(_input).Trim(), out var milli) ? milli / 1000.0 : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _last = null;
            }
            return _last;
        }
    }

    string? Find()
    {
        if (!Directory.Exists(root))
            return null;
        var sensors = Directory.EnumerateDirectories(root)
            .Select(dir => (Dir: dir, Name: ReadOrEmpty(Path.Combine(dir, "name"))))
            .Where(s => Drivers.Contains(s.Name))
            .OrderBy(s => Array.IndexOf(Drivers, s.Name));
        foreach (var (dir, _) in sensors)
        {
            // The labelled package or control temperature when there is one, else the first input.
            var labelled = Directory.EnumerateFiles(dir, "temp*_label")
                .FirstOrDefault(l => ReadOrEmpty(l) is "Tctl" or "Tdie" or "Package id 0");
            var input = labelled != null ? labelled.Replace("_label", "_input", StringComparison.Ordinal) : Path.Combine(dir, "temp1_input");
            if (File.Exists(input))
                return input;
        }
        return null;
    }

    static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
