using System.Reflection;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Adapters.Configuration;
using Semantico.Api.Data;
using Semantico.Api.Services;
using Semantico.Api.Web;
using Semantico.Api.Worker;
using Semantico.Api.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<SemanticoContext>((options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString(nameof(SemanticoContext)))
        .UseSnakeCaseNamingConvention();
});

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

// todo: use handfire :)
builder.Services.AddHangfire(hangfireConfiguration => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("Hangfire"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true
        }));

builder.Services.AddHangfireServer();
builder.Services.AddTransient<IJobService, JobService>();
builder.Services.AddTransient<IRecurringJobService, RecurringJobService>();
builder.Services.AddAdapters(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<IAccount, AccountClaimsResolver>();
builder.Services.AddTransient<IAccountService, IAccountService>();

builder.Services.AddAuthentication("BasicAuthentication")
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>
                ("BasicAuthentication", null);

var app = builder.Build();

var context = app.Services.GetRequiredService<SemanticoContext>();
await context.Database.MigrateAsync();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();

app.MapHangfireDashboard();

app.Run();