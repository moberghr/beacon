using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Semantico.Api.Data;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        services.AddDbContext<SemanticoContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
    })
    .Build();

using (var scope = host.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<SemanticoContext>();
    await context.Database.MigrateAsync();
}