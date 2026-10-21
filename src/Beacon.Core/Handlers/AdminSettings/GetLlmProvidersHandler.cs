using Beacon.Core.Authorization;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class GetLlmProvidersHandler(ILlmProviderCatalog catalog)
    : IRequestHandler<GetLlmProvidersQuery, GetLlmProvidersResult>
{
    public Task<GetLlmProvidersResult> Handle(GetLlmProvidersQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new GetLlmProvidersResult(catalog.AiEnabled, catalog.Registered));
    }
}

/// <summary>The LLM providers this host has installed, for the admin settings. For an Auditor or a SecurityAdmin.</summary>
[ProjectScopeExempt("gated by the Auditor and SecurityAdmin permissions: the installed providers, no project data", Policy = BeaconPermissions.AuditorOrSecurityAdminPolicy)]
public record GetLlmProvidersQuery : IRequest<GetLlmProvidersResult>;

/// <summary>
/// <paramref name="AiEnabled"/> false: AI is off on this host, so every provider can be stored and none is listed.
/// </summary>
public record GetLlmProvidersResult(bool AiEnabled, IReadOnlyList<LlmProviderInfo> Providers);
