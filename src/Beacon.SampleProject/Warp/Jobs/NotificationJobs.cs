using Beacon.Core.Notifications;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Encrypts recipient secrets stored before they were encrypted at rest. Idempotent. Never enqueued automatically: an
// operator runs it once, after every node runs a version that reads encrypted secrets, because a version older than
// that cannot read what it writes. It fails (and Warp shows it) if any recipient could not be encrypted.
public sealed class EncryptRecipientSecretsJob : IJob;

public sealed class EncryptRecipientSecretsJobHandler(
    IRecipientSecretEncryptionService encryptionService,
    ILogger<EncryptRecipientSecretsJobHandler> logger) : IJobHandler<EncryptRecipientSecretsJob>
{
    public async Task HandleAsync(EncryptRecipientSecretsJob message, CancellationToken cancellationToken)
    {
        var result = await encryptionService.EncryptStoredSecretsAsync(cancellationToken);

        logger.LogInformation(
            "Encrypt recipient secrets job: {Encrypted} of {Candidates} recipients encrypted, {Skipped} changed meanwhile",
            result.Encrypted,
            result.Candidates,
            result.SkippedAsChanged);
    }
}
