using System.Runtime.InteropServices;

namespace Beacon.SampleProject.Warp;

// Cancellation for the work done before app.Run(): the Warp migration and the recurring-job registration. The host's own
// shutdown token only fires once the host runs, so a stop signal or a stuck database during boot would otherwise leave
// that work unbounded. Dispose it before app.Run() so the host's lifetime takes over the signals again.
public sealed class BootCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly PosixSignalRegistration[] _registrations;

    public BootCancellation(TimeSpan timeout)
    {
        _source = new CancellationTokenSource(timeout);
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Cancel),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Cancel)
        ];
    }

    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _source.Dispose();
    }

    private void Cancel(PosixSignalContext context)
    {
        context.Cancel = true;
        _source.Cancel();
    }
}
