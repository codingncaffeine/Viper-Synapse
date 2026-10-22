using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>
/// Listens to keyboards and mice while a macro is recorded. Nothing is grabbed: what is typed still reaches
/// the desktop, as in Synapse. Events carry the kernel's timestamps, so the recorded gaps are the real ones
/// and not when a thread got round to reading them.
/// </summary>
public sealed class MacroRecorder : IDisposable
{
    readonly List<EvdevDevice> _nodes = [];
    readonly List<Thread> _threads = [];
    readonly List<RecordedEvent> _events = [];
    volatile bool _stopping;

    /// <summary>Every input node that sends keys or buttons, minus the ones named in <paramref name="skip"/>
    /// (nodes another session holds, whose keys arrive here through the virtual device instead).</summary>
    public static IReadOnlyList<string> KeyboardsAndMice(IEnumerable<string> skip)
    {
        var excluded = skip.ToHashSet();
        var nodes = new List<string>();
        foreach (var node in InputNode.Enumerate())
        {
            if (excluded.Contains(node.DevicePath))
                continue;
            try
            {
                using var device = EvdevDevice.Open(node.DevicePath);
                if (device.Capabilities(EventTypes.Key).Count > 0)
                    nodes.Add(node.DevicePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return nodes;
    }

    public static MacroRecorder Start(IEnumerable<string> nodePaths)
    {
        var recorder = new MacroRecorder();
        foreach (var path in nodePaths)
        {
            try { recorder._nodes.Add(EvdevDevice.Open(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var node in recorder._nodes)
        {
            var thread = new Thread(() => recorder.Listen(node)) { IsBackground = true, Name = "record " + node.Path };
            recorder._threads.Add(thread);
            thread.Start();
        }
        return recorder;
    }

    void Listen(EvdevDevice node)
    {
        var events = new InputEvent[64];
        try
        {
            while (!_stopping)
            {
                int n = node.Read(events, TimeSpan.FromMilliseconds(100));
                for (int i = 0; i < n; i++)
                {
                    var e = events[i];
                    RecordedEvent? recorded = e.Type switch
                    {
                        EventTypes.Key => new RecordedEvent(e.Time, e.Code, e.Value),
                        EventTypes.Rel when e.Code is RelativeAxes.Wheel or RelativeAxes.HWheel =>
                            new RecordedEvent(e.Time, 0, e.Value, IsWheel: true, Horizontal: e.Code == RelativeAxes.HWheel),
                        _ => null,
                    };
                    if (recorded is { } r)
                        lock (_events)
                            _events.Add(r);
                }
            }
        }
        catch (IOException) { }
    }

    /// <summary>Stops listening and returns what was caught, oldest first.</summary>
    public IReadOnlyList<RecordedEvent> Stop()
    {
        Dispose();
        lock (_events)
            return _events.OrderBy(e => e.Time).ToList();
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        foreach (var thread in _threads)
            thread.Join();
        foreach (var node in _nodes)
            node.Dispose();
    }
}
