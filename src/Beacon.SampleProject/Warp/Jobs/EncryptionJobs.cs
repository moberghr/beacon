using Beacon.Core.Services;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Re-encrypts with Beacon:EncryptionKey, in the current format, every stored value only a key in
// Beacon:PreviousEncryptionKeys decrypts or still in the legacy v1 format. Idempotent; enqueued once at every start
// (Program.cs). It logs how many values were still under a previous key or in the legacy format, and fails (Warp shows it)
// if a value could not be written.
public sealed class ReEncryptStoredValuesJob : IJob;

public sealed class ReEncryptStoredValuesJobHandler(IStoredValueReEncryptionService reEncryption)
    : IJobHandler<ReEncryptStoredValuesJob>
{
    public Task HandleAsync(ReEncryptStoredValuesJob message, CancellationToken cancellationToken)
        => reEncryption.ReEncryptAsync(cancellationToken);
}
