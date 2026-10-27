using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

internal static class PersistenceTestProvider
{
    public static ServiceProvider Build(string connectionString,
        PanelDirectoryFallback? fallback = null, TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration);

        if (fallback is not null) services.AddSingleton(fallback);
        if (timeProvider is not null) services.AddSingleton(timeProvider);

        return services.BuildServiceProvider();
    }
}
