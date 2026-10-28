using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EagleTunnelApi.Infrastructure.Persistence;

public sealed class DatabaseMigrationRunner(
    IDbContextFactory<AppDbContext> contextFactory,
    NpgsqlDataSource dataSource,
    ILogger<DatabaseMigrationRunner> logger)
{
    private const long AdvisoryLockKey = 0x4541474C4554554E;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var lockConnection = await dataSource.OpenConnectionAsync(cancellationToken);

        await AcquireLockAsync(lockConnection, cancellationToken);

        try
        {
            logger.LogInformation("Applying database migrations.");

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.MigrateAsync(cancellationToken);

            logger.LogInformation("Database migrations applied.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database migration failed.");
            throw;
        }
        finally
        {
            await ReleaseLockAsync(lockConnection);
        }
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(@key)";
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to release the database migration advisory lock.");
        }
    }
}
