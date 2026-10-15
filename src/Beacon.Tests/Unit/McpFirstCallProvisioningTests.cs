using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A user's first MCP call, end to end through the host's authentication order (API key, JWT bearer, authentication,
/// cookie, authorization) with the real recorder, actor accessor and project access evaluator over in-memory rows (no
/// database): the JWT middleware provisions the caller's Beacon user before it authenticates the request, recording that
/// as the system's; the request's caller is not resolved then, so the same request then reaches its projects as the
/// provisioned user.
/// </summary>
[TestFixture]
public class McpFirstCallProvisioningTests
{
    private const string Issuer = "https://login.microsoftonline.com/tenant-1/v2.0";
    private const string Audience = "api://beacon";
    private const string TenantId = "tenant-1";
    private const string UserOid = "user-oid-1";
    private const int ProvisionedUserId = 17;

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    [Test]
    public async Task AFirstCall_ProvisionsTheCaller_AsTheSystem_AndTheSameRequestReachesItsProjectsAsThatUser()
    {
        var rows = new Rows();
        await using var app = await StartAsync(rows);

        using var request = new HttpRequestMessage(HttpMethod.Post, McpDiscoveryPaths.McpPath);
        request.Headers.Add("Authorization", $"Bearer {MintToken()}");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = await response.Content.ReadFromJsonAsync<ProjectAccessBody>();
        access.Should().BeEquivalentTo(new ProjectAccessBody(ProvisionedUserId, [3], false), "the provisioned user reaches the project it was mapped to");
        rows.Users.Should().ContainSingle(x => x.ExternalId == UserOid).Which.Id.Should().Be(ProvisionedUserId);
        var provisioned = rows.Saved.OfType<SecurityAuditEvent>().Should().ContainSingle().Subject;
        provisioned.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.UserProvisioned, ActorKind = SecurityAuditActorKinds.System, ResourceId = "17" },
            x => x.ExcludingMissingMembers());
    }

    private static async Task<WebApplication> StartAsync(Rows rows)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        builder.Configuration["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        builder.Services.AddBeaconServices(builder.Configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            x.UserManagement.Enabled = true;
        });
        builder.Services.Configure<McpCallerOptions>(x =>
        {
            x.AllowedTenants = [TenantId];
            x.Users = new McpUserCallerOptions { Enabled = true, AllowAnyTenantUser = true, ProjectIds = [3], AutoProvision = true, DefaultRoleName = "Viewer" };
        });
        builder.Services.AddSingleton(rows.Factory());
        builder.Services.AddSingleton(SeededRoles());
        builder.Services.AddSingleton(Mock.Of<IApiKeyService>());
        builder.Services.AddBeaconCookieAuthentication("/beacon");
        builder.Services.AddBeaconJwtAuthentication(x =>
        {
            x.EnableBearerAuthentication = true;
            x.Validation = new JwtValidationOptions { SigningKey = SigningKey, ValidIssuer = Issuer, ValidAudience = Audience };
        });
        builder.Services.AddScoped<McpProjectContext>();
        builder.Services.AddBeaconApiAuthorization();
        var app = builder.Build();

        // The host's order (§1.9); the MCP endpoint stands in for the server, which fills its project context the same way.
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseBeaconJwtBearerAuthentication();
        app.UseAuthentication();
        app.UseMiddleware<BeaconCookieAuthMiddleware>();
        app.UseAuthorization();
        app.MapPost(McpDiscoveryPaths.McpPath, async (HttpContext context) =>
            {
                await ProjectContextFactory.PopulateAsync(context.RequestServices, context.RequestAborted);
                var projectContext = ProjectContextFactory.Create(context.RequestServices);

                return Results.Json(new ProjectAccessBody(projectContext.UserId, projectContext.AllowedProjectIds ?? [], projectContext.AccessUndetermined));
            })
            .RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);
        await app.StartAsync();

        return app;
    }

    // The system roles exist already; seeding them is not what this test is about.
    private static IRoleService SeededRoles()
    {
        var roles = new Mock<IRoleService>();
        roles
            .Setup(x => x.SeedSystemRolesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return roles.Object;
    }

    private static string MintToken()
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            [
                new Claim("sub", "pairwise-sub"),
                new Claim("oid", UserOid),
                new Claim("tid", TenantId),
                new Claim("azp", "aiproxy-client"),
                new Claim("scp", "Mcp.Access"),
                new Claim("preferred_username", "ana@contoso.example"),
                new Claim("name", "Ana Analyst")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record ProjectAccessBody(int? UserId, List<int> AllowedProjectIds, bool AccessUndetermined);

    /// <summary>
    /// The deployment's rows: the super admin (setup is complete), the Viewer role, open project 3. A save stores a user the
    /// code added as the database would: with its generated id and its roles.
    /// </summary>
    private sealed class Rows
    {
        private readonly List<BeaconRole> _roles = [new BeaconRole { Id = 1, Name = "Viewer", Level = RoleService.RoleLevels.Viewer, IsSystemRole = true }];

        public List<BeaconUser> Users { get; } = [new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsSuperAdmin = true, IsEnabled = true, IsInternalUser = true }];

        public List<object> Saved { get; } = [];

        public IDbContextFactory<BeaconContext> Factory()
        {
            var projects = new List<Project> { new() { Id = 3, Name = "Project 3" } };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved),
                        [typeof(BeaconRole)] = RecordingBeaconContext.MemorySet(_roles, Saved),
                        [typeof(BeaconUserRole)] = RecordingBeaconContext.MemorySet(
                            Users
                                .SelectMany(x => x.UserRoles)
                                .ToList(),
                            Saved),
                        [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, Saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(new List<DataSource>(), Saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(new List<ProjectDataSource>(), Saved),
                        [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(new List<ProjectMember>(), Saved),
                        [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet(new List<ProjectDocumentation>(), Saved),
                        [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet(new List<GitHubRepository>(), Saved),
                        [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(new List<SecurityAuditEvent>(), Saved)
                    },
                    Saved,
                    new SqlCapture(),
                    StoreAddedUsers));

            return factory.Object;
        }

        private void StoreAddedUsers()
        {
            var added = Saved
                .OfType<BeaconUser>()
                .Where(x => !Users.Contains(x))
                .ToList();
            foreach (var user in added)
            {
                user.Id = ProvisionedUserId;
                foreach (var userRole in user.UserRoles)
                {
                    userRole.UserId = user.Id;
                    userRole.Role = _roles
                        .Where(x => x.Id == userRole.RoleId)
                        .First();
                }

                Users.Add(user);
            }
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
