using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class SetSubscriptionSlaHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider)
    : IRequestHandler<SetSubscriptionSlaCommand>
{
    private const int MinSlaHours = 1;
    private const int MaxSlaHours = 720; // 30 days

    public async Task Handle(SetSubscriptionSlaCommand request, CancellationToken cancellationToken)
    {
        if (request.SlaHours.HasValue)
        {
            if (request.SlaHours.Value < MinSlaHours || request.SlaHours.Value > MaxSlaHours)
            {
                throw new InvalidOperationException(
                    $"SlaHours must be between {MinSlaHours} and {MaxSlaHours}.");
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == request.SubscriptionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription == null)
        {
            throw new InvalidOperationException($"Subscription {request.SubscriptionId} not found.");
        }

        var before = subscription.SlaHours;
        if (before == request.SlaHours)
        {
            return;
        }

        subscription.SlaHours = request.SlaHours;
        subscription.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.SubscriptionSlaChanged)
                .On(SecurityAuditResources.Subscription, request.SubscriptionId)
                .With(new SecurityAuditDetails()
                    .Add("slaHoursBefore", before)
                    .Add("slaHoursAfter", request.SlaHours)),
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record SetSubscriptionSlaCommand(int SubscriptionId, int? SlaHours) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Write)
    ];
}
