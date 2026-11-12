using System.Diagnostics;
using System.Globalization;

namespace ViperSynapse.Service;

/// <summary>
/// While a mouse is remapped its motion reaches the desktop through our virtual device, and the desktop treats that
/// as a new pointer with its default acceleration (on KDE, adaptive) instead of the mouse's own (often flat). This
/// copies the real mouse's acceleration profile and speed onto the virtual device, through KWin's input devices on
/// the session bus (busctl). Elsewhere (no KWin) it does nothing.
/// </summary>
static class PointerMirror
{
    const string Virtual = "Viper Synapse virtual input";
    const string Iface = "org.kde.KWin.InputDevice";

    /// <param name="mouseName">The real mouse's model name, e.g. "Razer Basilisk V4 Pro" (its input nodes' names contain it).</param>
    public static void Apply(string mouseName)
    {
        try
        {
            var devices = Directory.GetDirectories("/sys/class/input", "event*")
                .Select(d => (Event: Path.GetFileName(d), Name: Read(Path.Combine(d, "device", "name"))))
                .ToList();
            var target = devices.FirstOrDefault(d => d.Name == Virtual).Event;
            if (target == null)
                return;
            // The real mouse's pointer node: the first of its nodes KWin knows that can be accelerated.
            foreach (var (ev, _) in devices.Where(d => d.Name.Contains(mouseName, StringComparison.Ordinal) && d.Event != target))
            {
                if (Get(ev, "supportsPointerAcceleration") != "b true")
                    continue;
                string? flat = Get(ev, "pointerAccelerationProfileFlat"), speed = Get(ev, "pointerAcceleration");
                if (flat == null || speed == null)
                    continue;
                Set(target, "pointerAccelerationProfileFlat", "b", flat[2..]);
                Set(target, "pointerAccelerationProfileAdaptive", "b", flat == "b true" ? "false" : "true");
                Set(target, "pointerAcceleration", "d", speed[2..]);
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"pointer settings: {e.Message}");
        }
    }

    static string Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return ""; }
    }

    static string? Get(string ev, string property)
    {
        var (code, output) = Busctl("get-property", "org.kde.KWin", $"/org/kde/KWin/InputDevice/{ev}", Iface, property);
        return code == 0 ? output.Trim() : null;
    }

    static void Set(string ev, string property, string type, string value) =>
        Busctl("set-property", "org.kde.KWin", $"/org/kde/KWin/InputDevice/{ev}", Iface, property, type, value);

    static (int Code, string Output) Busctl(params string[] args)
    {
        var info = new ProcessStartInfo("busctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--user");
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("busctl did not start");
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(3000))
            p.Kill();
        return (p.ExitCode, output.ToString(CultureInfo.InvariantCulture));
    }
}
