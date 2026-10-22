namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Marks the registrations <c>Moberg.Beacon.AI</c> ships. <see cref="LlmProviderFactory"/> uses one only when no other
/// registration claims its provider, so a host's own registration wins whether it was added before or after
/// <c>AddBeaconAI</c>.
/// </summary>
internal interface IBuiltInLlmProviderRegistration : ILlmProviderRegistration
{
}
