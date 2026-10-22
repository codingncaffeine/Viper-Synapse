using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Core.Engine;

/// <summary>
/// Turns one device's key presses into what its keymap says. The device is grabbed, so every event comes
/// through here: unbound keys and pointer motion pass straight to the sink. A key's binding is fixed at
/// the moment it goes down, so letting go of Hypershift first still releases what the key started.
/// Called from the device's input thread only; turbo and macros run as tasks of their own.
/// </summary>
public sealed class RemapEngine
{
    readonly IInputSink _sink;
    readonly IEngineHost _host;
    readonly TimeProvider _time;
    readonly MacroRunner _macros;
    readonly Dictionary<ushort, Binding> _held = [];
    readonly Dictionary<ushort, CancellationTokenSource> _running = [];
    readonly Dictionary<Guid, CancellationTokenSource> _toggled = [];
    int _hypershiftHeld;

    public RemapEngine(IInputSink sink, IEngineHost host, TimeProvider? time = null)
    {
        _sink = sink;
        _host = host;
        _time = time ?? TimeProvider.System;
        _macros = new MacroRunner(sink, host, _time);
    }

    public Keymap Keymap { get; set; } = Keymap.Empty("Keymap 1");

    /// <summary>What a control that sends no key of its own (a thumbstick direction) does when unbound: the device's
    /// default for it, W for the stick's up, say. Null leaves it doing nothing.</summary>
    public Func<ushort, Binding?>? Defaults { get; set; }

    /// <summary>Any key press stops running macros (Synapse's "Macro and Keystroke Interruption: any key").</summary>
    public MacroInterruption Interruption { get; set; } = MacroInterruption.DedicatedKey;

    bool _hypershiftButton;

    /// <summary>The keypad's own Hypershift button, reported by its firmware rather than as a key.</summary>
    public void HypershiftButton(bool held)
    {
        if (held == _hypershiftButton)
            return;
        _hypershiftButton = held;
        _hypershiftHeld = Math.Max(0, _hypershiftHeld + (held ? 1 : -1));
    }

    public bool HypershiftActive => _hypershiftHeld > 0;

    /// <summary>A key or button from the device: 1 down, 0 up, 2 auto-repeat.</summary>
    public void Key(ushort code, int value)
    {
        if (value == 2)
        {
            // Auto-repeat belongs to keys that pass through; a remapped key makes its own repeats.
            if (_held.TryGetValue(code, out var heldBinding) && heldBinding is DefaultBinding)
                Emit(code, 2);
            return;
        }
        if (value == 1)
        {
            if (_held.ContainsKey(code))
                return;
            if (Interruption == MacroInterruption.AnyKey)
                StopMacros();
            var binding = Keymap.Resolve(code, HypershiftActive);
            if (binding is DefaultBinding && SourceKeys.IsStick(code))
                binding = Defaults?.Invoke(code) ?? new DisableBinding();
            _held[code] = binding;
            Press(code, binding);
        }
        else if (_held.Remove(code, out var binding))
        {
            Release(code, binding);
        }
    }

    /// <summary>Wheel movement: each direction is a control of its own and can be rebound.</summary>
    public void Wheel(ushort axis, int value)
    {
        bool horizontal = axis is RelativeAxes.HWheel or RelativeAxes.HWheelHiRes;
        bool hiRes = axis is RelativeAxes.WheelHiRes or RelativeAxes.HWheelHiRes;
        ushort source = horizontal
            ? value > 0 ? SourceKeys.WheelRight : SourceKeys.WheelLeft
            : value > 0 ? SourceKeys.WheelUp : SourceKeys.WheelDown;
        var binding = Keymap.Resolve(source, HypershiftActive);
        if (binding is DefaultBinding)
        {
            _sink.Relative(axis, value);
            return;
        }
        // A bound wheel fires once per detent; the high-resolution twin of the same detent is dropped.
        if (hiRes)
            return;
        for (int i = 0; i < Math.Abs(value); i++)
        {
            Press(source, binding);
            Release(source, binding);
        }
    }

    /// <summary>Pointer motion and anything else relative passes through.</summary>
    public void Relative(ushort axis, int value) => _sink.Relative(axis, value);

    public void Sync() => _sink.Sync();

    /// <summary>Stops every turbo and macro and lets go of every key the engine holds down: the device was
    /// unplugged, the profile changed or the engine is shutting down.</summary>
    public void Reset()
    {
        foreach (var (code, binding) in _held.ToList())
            Release(code, binding);
        _held.Clear();
        lock (_running)
        {
            foreach (var cts in _running.Values.Concat(_toggled.Values))
                cts.Cancel();
            _running.Clear();
        }
        _toggled.Clear();
        _hypershiftHeld = 0;
        _hypershiftButton = false;
    }

    /// <summary>Stops every running and toggled macro (any-key interruption). Held turbo keys keep going.</summary>
    void StopMacros()
    {
        lock (_running)
        {
            foreach (var (key, cts) in _running.ToList())
                if (_held.TryGetValue(key, out var b) && b is MacroBinding or TextBinding || !_held.ContainsKey(key))
                {
                    cts.Cancel();
                    _running.Remove(key);
                }
            foreach (var cts in _toggled.Values)
                cts.Cancel();
            _toggled.Clear();
        }
    }

    void Press(ushort code, Binding binding)
    {
        switch (binding)
        {
            case DefaultBinding:
                Emit(code, 1);
                break;
            case KeyboardBinding k when k.Turbo:
                StartTurbo(code, k.TurboRate, () => Tap(k));
                break;
            case KeyboardBinding k:
                foreach (var m in ModifierCodes(k.Modifiers))
                    _sink.Key(m, 1);
                Emit(k.Key, 1);
                break;
            case MouseBinding m when m.Turbo || IsWheel(m.Function):
                if (m.Turbo) StartTurbo(code, m.TurboRate, () => Click(m.Function));
                else Click(m.Function);
                break;
            case MouseBinding { Function: MouseFunction.DoubleClick }:
                Click(MouseFunction.DoubleClick);
                break;
            case MouseBinding m:
                Emit(ButtonOf(m.Function), 1);
                break;
            case MacroBinding macro:
                StartMacro(code, macro);
                break;
            case HypershiftBinding:
                _hypershiftHeld++;
                break;
            case SwitchProfileBinding p:
                _host.SwitchProfile(p.Direction, p.ProfileId);
                break;
            case SwitchKeymapBinding k:
                _host.SwitchKeymap(k.Direction, k.Keymap);
                break;
            case LaunchBinding launch:
                _host.Launch(launch.Command);
                break;
            case TextBinding text:
                Run(code, ct => _macros.PlayAsync(TextTyper.Steps(text.Text).ToList<MacroStep>(), ct));
                break;
        }
    }

    void Release(ushort code, Binding binding)
    {
        switch (binding)
        {
            case DefaultBinding:
                Emit(code, 0);
                break;
            case KeyboardBinding { Turbo: true } or MouseBinding { Turbo: true }:
                Stop(code);
                break;
            case KeyboardBinding k:
                Emit(k.Key, 0);
                foreach (var m in ModifierCodes(k.Modifiers).Reverse())
                    _sink.Key(m, 0);
                if (k.Modifiers != Modifiers.None)
                    _sink.Sync();
                break;
            case MouseBinding m when !IsWheel(m.Function) && m.Function != MouseFunction.DoubleClick:
                Emit(ButtonOf(m.Function), 0);
                break;
            case MacroBinding { Playback: MacroPlayback.WhileHeld }:
                Stop(code);
                break;
            case HypershiftBinding:
                _hypershiftHeld = Math.Max(0, _hypershiftHeld - 1);
                break;
        }
    }

    void StartMacro(ushort code, MacroBinding binding)
    {
        var macro = _host.FindMacro(binding.MacroId);
        if (macro == null)
            return;
        if (binding.Playback == MacroPlayback.Toggle)
        {
            if (_toggled.Remove(binding.MacroId, out var running))
            {
                running.Cancel();
                return;
            }
            var cts = new CancellationTokenSource();
            _toggled[binding.MacroId] = cts;
            Launch(_macros.PlayAsync(macro, binding.Playback, binding.Times, cts.Token));
            return;
        }
        // Pressing the key of a macro that is still playing starts nothing new.
        lock (_running)
            if (_running.ContainsKey(code))
                return;
        Run(code, ct => _macros.PlayAsync(macro, binding.Playback, binding.Times, ct));
    }

    void StartTurbo(ushort code, int rate, Action tap)
    {
        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(rate, 1, 100));
        Run(code, async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                tap();
                try { await Task.Delay(period, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    /// <summary>Runs a task that belongs to a held key: releasing the key (or <see cref="Reset"/>) cancels it.
    /// A task that ends on its own clears its slot.</summary>
    void Run(ushort code, Func<CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        lock (_running)
            _running[code] = cts;
        Launch(Task.Run(async () =>
        {
            try { await work(cts.Token).ConfigureAwait(false); }
            finally
            {
                lock (_running)
                    if (_running.TryGetValue(code, out var current) && current == cts)
                        _running.Remove(code);
            }
        }));
    }

    void Stop(ushort code)
    {
        CancellationTokenSource? cts;
        lock (_running)
            _running.Remove(code, out cts);
        cts?.Cancel();
    }

    void Launch(Task task) => task.ContinueWith(t => _host.Report(t.Exception!.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);

    void Tap(KeyboardBinding k)
    {
        foreach (var m in ModifierCodes(k.Modifiers))
            _sink.Key(m, 1);
        Emit(k.Key, 1);
        Emit(k.Key, 0);
        foreach (var m in ModifierCodes(k.Modifiers).Reverse())
            _sink.Key(m, 0);
        _sink.Sync();
    }

    void Click(MouseFunction function)
    {
        switch (function)
        {
            case MouseFunction.ScrollUp: Scroll(RelativeAxes.Wheel, 1); break;
            case MouseFunction.ScrollDown: Scroll(RelativeAxes.Wheel, -1); break;
            case MouseFunction.ScrollRight: Scroll(RelativeAxes.HWheel, 1); break;
            case MouseFunction.ScrollLeft: Scroll(RelativeAxes.HWheel, -1); break;
            case MouseFunction.DoubleClick:
                for (int i = 0; i < 2; i++) { Emit(KeyCodes.BtnLeft, 1); Emit(KeyCodes.BtnLeft, 0); }
                break;
            default:
                Emit(ButtonOf(function), 1);
                Emit(ButtonOf(function), 0);
                break;
        }
    }

    void Scroll(ushort axis, int clicks)
    {
        _sink.Relative(axis, clicks);
        _sink.Relative(axis == RelativeAxes.Wheel ? RelativeAxes.WheelHiRes : RelativeAxes.HWheelHiRes, clicks * 120);
        _sink.Sync();
    }

    void Emit(ushort code, int value)
    {
        _sink.Key(code, value);
        _sink.Sync();
    }

    static bool IsWheel(MouseFunction f) => f is MouseFunction.ScrollUp or MouseFunction.ScrollDown or MouseFunction.ScrollLeft or MouseFunction.ScrollRight;

    static ushort ButtonOf(MouseFunction f) => f switch
    {
        MouseFunction.LeftClick => KeyCodes.BtnLeft,
        MouseFunction.RightClick => KeyCodes.BtnRight,
        MouseFunction.ScrollClick => KeyCodes.BtnMiddle,
        MouseFunction.Back => KeyCodes.BtnSide,
        MouseFunction.Forward => KeyCodes.BtnExtra,
        _ => KeyCodes.BtnLeft,
    };

    static IEnumerable<ushort> ModifierCodes(Modifiers m)
    {
        if (m.HasFlag(Modifiers.Ctrl)) yield return KeyCodes.LeftCtrl;
        if (m.HasFlag(Modifiers.Shift)) yield return KeyCodes.LeftShift;
        if (m.HasFlag(Modifiers.Alt)) yield return KeyCodes.LeftAlt;
        if (m.HasFlag(Modifiers.Super)) yield return KeyCodes.LeftMeta;
    }
}
