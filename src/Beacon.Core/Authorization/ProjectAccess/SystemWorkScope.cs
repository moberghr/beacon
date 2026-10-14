namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>The default <see cref="ISystemWorkScope"/>: a flag per DI scope.</summary>
internal sealed class SystemWorkScope : ISystemWorkScope
{
    public bool IsSystemWork { get; private set; }

    public void MarkAsSystemWork()
    {
        IsSystemWork = true;
    }
}
