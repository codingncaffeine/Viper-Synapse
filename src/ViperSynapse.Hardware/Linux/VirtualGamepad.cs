using ViperSynapse.Core.Input;

namespace ViperSynapse.Hardware.Linux;

/// <summary>
/// The gamepad keys can stand in for: a uinput device with an Xbox 360 pad's buttons and axes, under that pad's
/// USB ids and the codes the kernel's xpad driver uses, so SDL, Steam and games take it for one. Made the first
/// time a key works it, so nothing shows up as a controller for people who never map one. Thread-safe.
/// </summary>
public sealed class VirtualGamepad : IDisposable
{
    readonly Lock _gate = new();
    readonly GamepadState _state = new();
    readonly Func<UinputDevice> _create;
    UinputDevice? _device;
    bool _failed;

    public VirtualGamepad(Func<UinputDevice>? create = null) =>
        _create = create ?? (() => UinputDevice.CreateGamepad("Viper Synapse virtual gamepad"));

    public void Set(GamepadControl control, bool down)
    {
        lock (_gate)
        {
            var events = _state.Set(control, down);
            if (events.Count == 0 || _failed)
                return;
            try
            {
                _device ??= _create();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Said once; the keys simply do nothing as a gamepad.
                _failed = true;
                Console.Error.WriteLine($"Virtual gamepad: {e.Message}");
                return;
            }
            foreach (var e in events)
                _device.Write(e.Type, e.Code, e.Value);
            _device.Sync();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
