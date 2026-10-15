namespace Beacon.Tests.Common;

/// <summary>
/// A test context whose SaveChanges does not reach EF's own (so EF's <c>SavingChanges</c> is never raised) and tells its
/// saves itself, so <see cref="RecordingSecurityAudit"/> can check that a staged event is followed by a save.
/// </summary>
internal interface IObservableSaves
{
    /// <summary>Raised by every SaveChanges, before it saves anything.</summary>
    event Action? Saving;
}
