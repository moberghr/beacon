using System.Security.Cryptography;
using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Tests.Unit.SavedQueryTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Microsoft.Extensions.Time.Testing;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// A project (1, linking source 10), its creator (user 3, <c>ext-ada</c>, an Editor), a saved query (5) with an approved
/// active version, and embed grants on it, over the list-backed store (no database, §4.7); a signing key generated per
/// run (never a real secret, §1.2) and a clock the test moves.
/// </summary>
internal sealed class EmbedTestKit
{
    public const int ProjectId = 1;
    public const int DataSourceId = 10;
    public const int QueryId = 5;
    public const int CreatorId = 3;
    public const string CreatorExternalId = "ext-ada";
    public const string Origin = "https://portal.example.com";

    public static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private int _lookups;

    public EmbedTestKit(string sql = "SELECT id, email FROM loans WHERE branch = {branch} AND region = {region}")
    {
        Data.Project(ProjectId, "A", Data.DataSource(DataSourceId, "warehouse"));
        Query = Data.Query(QueryId, null, [Step(1, DataSourceId, sql, Parameter("branch", ParameterType.Number), Parameter("region", ParameterType.String))]);
        Creator = new BeaconUser
        {
            Id = CreatorId,
            ExternalId = CreatorExternalId,
            UserName = "ada",
            IsEnabled = true,
            CreatedTime = Now.UtcDateTime.AddDays(-30),
            UserRoles = [new BeaconUserRole { UserId = CreatorId, Role = new BeaconRole { Name = "Editor", Level = RoleService.RoleLevels.Editor } }]
        };
        Data.Store.ListFor<BeaconUser>().Add(Creator);
    }

    public SavedQueryTestData Data { get; } = new();

    public Query Query { get; }

    public BeaconUser Creator { get; }

    public FakeTimeProvider Clock { get; } = new(Now);

    public EmbedOptions Options { get; } = new() { Enabled = true, SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

    public BeaconConfiguration Configuration { get; } = new();

    public EmbedTokenCodec Codec => new(Microsoft.Extensions.Options.Options.Create(Options), Clock);

    /// <summary>How many contexts the kit's resolvers opened: one per grant lookup.</summary>
    public int Lookups => _lookups;

    public EmbedGrantResolver Resolver()
    {
        var store = Data.Store.Factory().Object;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken x) =>
            {
                Interlocked.Increment(ref _lookups);

                return store.CreateDbContextAsync(x);
            });

        return new(factory.Object, Codec, Configuration, Microsoft.Extensions.Options.Options.Create(Options), Clock);
    }

    /// <summary>A grant on the query's active version: <c>region</c> fixed to <c>EU</c>, <c>branch</c> locked.</summary>
    public (EmbedGrant Grant, string Secret) Grant(DateTime? revoked = null, int expiresInDays = 30, string? createdBy = CreatorExternalId)
    {
        var (secret, hash) = EmbedGrantSecret.Create();
        var grant = new EmbedGrant
        {
            Id = Data.Store.NextId(),
            PublicId = EmbedGrantSecret.NewPublicId(),
            ProjectId = ProjectId,
            QueryId = QueryId,
            QueryVersionId = Query.ActiveVersionId!.Value,
            Name = "Loan book",
            AllowedOriginsJson = JsonSerializer.Serialize(new[] { Origin }),
            FixedParametersJson = "{\"region\":\"EU\"}",
            LockedParameterNamesJson = "[\"branch\"]",
            SecretHash = hash,
            ExpiresTime = Now.UtcDateTime.AddDays(expiresInDays),
            RevokedTime = revoked,
            CreatedTime = Now.UtcDateTime,
            CreatedByUserId = createdBy
        };
        Data.Store.ListFor<EmbedGrant>().Add(grant);

        return (grant, secret);
    }

    /// <summary>Approves a newer version and makes it active: every grant on the older one is superseded.</summary>
    public void Supersede()
    {
        var next = Data.AddVersion(Query, 2, QueryVersionStatus.Active, [Step(1, DataSourceId, "SELECT 2")], approval: ApprovalStatus.Approved);
        Query.ActiveVersionId = next.Id;
        Query.ActiveVersion = next;
    }

    /// <summary>A token for <paramref name="grant"/> with <c>branch</c> = <paramref name="branch"/>.</summary>
    public async Task<string> TokenAsync(EmbedGrant grant, string branch = "7", int ttlSeconds = 300)
    {
        var issued = await Codec.MintAsync(grant.PublicId, new Dictionary<string, string> { ["branch"] = branch }, TimeSpan.FromSeconds(ttlSeconds));

        return issued.Token;
    }

    /// <summary>The actor the embed middleware's principal resolves to for <paramref name="grant"/>.</summary>
    public BeaconActor Actor(EmbedGrant grant, EmbedCredential credential) =>
        new(null, false)
        {
            Kind = BeaconPrincipalKind.Embed,
            EmbedGrantId = grant.Id,
            EmbedCredential = credential,
            BeaconUserId = CreatorId,
            IsActiveUser = true,
            RoleLevel = RoleService.RoleLevels.Editor,
            AllowedProjectIds = [ProjectId]
        };

    public IOptions<EmbedOptions> EmbedOptions() => Microsoft.Extensions.Options.Options.Create(Options);
}
