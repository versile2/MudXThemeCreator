using Microsoft.EntityFrameworkCore;
using MudXtra.ThemeCreator.Infrastructure.Data;
using MudXtra.ThemeCreator.UI.Startup;

namespace MudXtra.ThemeCreator.UI.Extensions;

public static class IServiceCollectionExtensions
{
    public static Task AddThemeDataBaseConnection(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = PostgresReadiness.ResolveConnectionString(config);
        services.AddDbContextFactory<ThemeDbContext>(options => options.UseNpgsql(connectionString));
        return Task.CompletedTask;
    }
}
