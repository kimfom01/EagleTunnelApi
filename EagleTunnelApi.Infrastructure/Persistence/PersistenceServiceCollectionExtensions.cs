using EagleTunnelApi.Application.Auditing;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddSingleton<DatabaseMigrationRunner>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PanelDirectoryFallback>(PanelDirectoryFallback.Empty);
        services.TryAddSingleton<IPanelDirectory, PanelDirectoryStore>();
        services.TryAddSingleton<IPanelMirrorWriter, PanelMirrorWriter>();
        services.TryAddSingleton<ICommentBackfill, CommentBackfillService>();

        return services;
    }
}
