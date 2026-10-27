using EagleTunnelApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

public class PersistenceRegistrationTests
{
    [Fact]
    public void AddPersistence_registers_context_and_factory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=eagletunnelapi;Username=postgres;Password=postgres"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        Assert.NotNull(provider.GetRequiredService<IDbContextFactory<AppDbContext>>());
        Assert.NotNull(provider.GetRequiredService<DatabaseMigrationRunner>());
    }

    [Fact]
    public void AddPersistence_requires_a_connection_string_when_resolved()
    {
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AppDbContext>());

        Assert.Contains("ConnectionStrings:Postgres", exception.Message);
    }
}
