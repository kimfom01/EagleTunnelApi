using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EagleTunnelApi.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var connectionString = configuration.GetConnectionString("Postgres");

            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    "ConnectionStrings:Postgres is required to configure persistence.");

            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });

        services.AddDbContextFactory<AppDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<AppDbContext>(serviceProvider =>
            serviceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        return services;
    }
}
