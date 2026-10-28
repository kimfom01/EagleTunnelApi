using EagleTunnelApi.Health;
using EagleTunnelApi.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace EagleTunnelApi.Tests.Health;

[Collection(PostgresCollection.Name)]
public class PostgresHealthCheckTests
{
    private readonly PostgresContainerFixture _fixture;

    public PostgresHealthCheckTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CheckHealthAsync_reports_healthy_when_postgres_is_reachable()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var healthCheck = new PostgresHealthCheck(dataSource);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
