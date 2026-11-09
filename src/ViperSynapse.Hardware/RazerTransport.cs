using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>
/// Sends Razer control reports to a device's control interface and reads the answers. The report
/// travels as feature report 0, so the buffer on the wire is the report id followed by 90 bytes.
/// One transport per device; calls are serialised because the device answers one command at a time.
/// </summary>
public sealed class RazerTransport : IDisposable
{
    readonly HidrawDevice _device;
    readonly Lock _gate = new();

    /// <summary>How long the device gets to prepare its answer before the first read. Razer firmware
    /// needs well under a millisecond for most commands; a busy answer waits again.</summary>
    public TimeSpan AnswerDelay { get; init; } = TimeSpan.FromMilliseconds(1);
    public int BusyRetries { get; init; } = 20;

    public RazerTransport(HidrawDevice device) => _device = device;

    public string Path => _device.Path;

    /// <summary>How many answers to other requests are read past before giving up on this one's.</summary>
    const int StaleRetries = 6;

    public RazerReport Send(RazerReport request)
    {
        lock (_gate)
        {
            var wire = new byte[RazerReport.Size + 1];
            request.Bytes.CopyTo(wire.AsSpan(1));
            _device.SendFeature(wire);
            for (int attempt = 0, stale = 0; ; attempt++)
            {
                Thread.Sleep(AnswerDelay);
                Array.Clear(wire);
                _device.ReceiveFeature(wire);
                var answer = RazerReport.Parse(wire.AsSpan(1));
                if (answer.Status == ReportStatus.Busy && attempt < BusyRetries)
                    continue;
                // A wireless receiver can hand back an answer to an earlier request (one that timed out, or another
                // program's): that is not this one's answer, so read again rather than take it for this command's.
                if (answer.Status != ReportStatus.Busy && !answer.Answers(request) && stale++ < StaleRetries)
                    continue;
                return answer;
            }
        }
    }

    public void Dispose() => _device.Dispose();
}
