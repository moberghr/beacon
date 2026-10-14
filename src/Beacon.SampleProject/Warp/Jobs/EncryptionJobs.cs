using Beacon.Core.Services;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Re-encrypts with Beacon:EncryptionKey every stored value only a key in Beacon:PreviousEncryptionKeys decrypts.
// Idempotent; enqueued once at startup while a previous key is configured (Program.cs). It logs how many values were still
// under a previous key, and fails (Warp shows it) if a value could not be written.
public sealed class ReEncryptStoredValuesJob : IJob;

public sealed class ReEncryptStoredValuesJobHandler(IStoredValueReEncryptionService reEncryption)
    : IJobHandler<ReEncryptStoredValuesJob>
{
    public Task HandleAsync(ReEncryptStoredValuesJob message, CancellationToken cancellationToken)
        => reEncryption.ReEncryptAsync(cancellationToken);
}
