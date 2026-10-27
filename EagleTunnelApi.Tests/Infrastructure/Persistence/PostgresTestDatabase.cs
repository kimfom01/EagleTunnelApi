using Npgsql;
using Respawn;
using Respawn.Graph;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private Respawner? _respawner;

    private PostgresTestDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;

        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync(
        string adminConnectionString,
        CancellationToken cancellationToken = default)
    {
        var databaseName = $"eagletunnelapi_test_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new PostgresTestDatabase(adminConnectionString, databaseName);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")]
        });

        await _respawner.ResetAsync(connection);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(_databaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}
