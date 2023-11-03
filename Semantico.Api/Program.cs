using System.Reflection;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Adapters.Configuration;
using Semantico.Api.Data;
using Semantico.Api.Helpers;
using Semantico.Api.Services;
using Semantico.Api.Types;
using Semantico.Api.Web;
using Semantico.Api.Worker;
using Semantico.Api.Worker.Repositories;
using Semantico.Api.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerWithApiKey(Constants.SemanticoApiKeyHeaderName);

builder.Services.AddDbContext<SemanticoContext>((options) =>
{
    options.UseNpgsql(
            builder.Configuration.GetConnectionString(nameof(SemanticoContext)),
            x => x.MigrationsHistoryTable("__SemanticoMigrationsHistory", "semantico"))
        .UseSnakeCaseNamingConvention();
});

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

builder.Services.AddHangfire(hangfireConfiguration => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString(nameof(SemanticoContext)),
        new PostgreSqlStorageOptions
        {
            SchemaName = "semantico_hangfire",
            PrepareSchemaIfNecessary = true
        }));

builder.Services.AddHangfireServer();
builder.Services.AddTransient<IJobRepository, JobRepository>();
builder.Services.AddTransient<IJobService, JobService>();
builder.Services.AddTransient<INotificationService, NotificationService>();
builder.Services.AddSingleton<IRecurringJobService, RecurringJobService>();
builder.Services.AddAdapters(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<IAccount, AccountClaimsResolver>();
builder.Services.AddTransient<IAccountService, AccountService>();

builder.Services.AddAuthentication(Constants.SemanticoApiKeyHeaderName)
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>
                (Constants.SemanticoApiKeyHeaderName, null);

var app = builder.Build();

using var scope = app.Services.CreateScope();
await scope.ServiceProvider.GetRequiredService<SemanticoContext>().Database.MigrateAsync();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();

app.MapHangfireDashboard(new DashboardOptions
{
    Authorization = new[]
        {
            new HangfireAuthorizationFilter()
        }
});

app.Run();