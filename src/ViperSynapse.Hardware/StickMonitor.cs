using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>
/// Follows a keypad's thumbstick without taking it (analog mode, where the gamepad node stays with the system), for
/// the live position circle on Customize. In 4-way and 8-way mode the device session reports the stick instead.
/// </summary>
public sealed class StickMonitor : IDisposable
{
    readonly EvdevDevice _node;
    readonly Thread _thread;
    volatile bool _stopping;
    double _x, _y;

    /// <summary>The stick moved: x and y from -1 to 1, y negative upwards.</summary>
    public event Action<double, double>? Moved;

    StickMonitor(EvdevDevice node)
    {
        _node = node;
        _thread = new Thread(Listen) { IsBackground = true, Name = "stick " + node.Path };
    }

    public static StickMonitor Start(string path)
    {
        var monitor = new StickMonitor(EvdevDevice.Open(path));
        monitor._thread.Start();
        return monitor;
    }

    void Listen()
    {
        var events = new InputEvent[64];
        try
        {
            while (!_stopping)
            {
                int n = _node.Read(events, TimeSpan.FromMilliseconds(100));
                for (int i = 0; i < n; i++)
                {
                    var e = events[i];
                    if (e.Type == EventTypes.Abs && e.Code is 0 or 3)
                        _x = Math.Clamp(e.Value / 32767.0, -1, 1);
                    else if (e.Type == EventTypes.Abs && e.Code is 1 or 4)
                        _y = Math.Clamp(e.Value / 32767.0, -1, 1);
                    else if (e.Type == EventTypes.Syn && e.Code == EventTypes.SynReport)
                        Moved?.Invoke(_x, _y);
                }
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join();
        _node.Dispose();
    }
}
