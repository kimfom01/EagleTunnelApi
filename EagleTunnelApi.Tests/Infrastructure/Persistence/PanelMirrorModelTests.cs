using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

public class PanelMirrorModelTests
{
    [Fact]
    public void Panel_tables_and_columns_are_mapped()
    {
        using var context = CreateContext();

        var client = GetEntityType<PanelClient>(context);
        Assert.Equal("panel_clients", client.GetTableName());
        Assert.Equal("panel_client_id",
            client.FindProperty(nameof(PanelClient.PanelClientId))!.GetColumnName());
        Assert.Equal("email_normalized",
            client.FindProperty(nameof(PanelClient.EmailNormalized))!.GetColumnName());
        Assert.Equal("timestamptz",
            client.FindProperty(nameof(PanelClient.ExpiryTime))!.GetColumnType());
        Assert.Equal("legacy_comment",
            client.FindProperty(nameof(PanelClient.LegacyComment))!.GetColumnName());

        var inbound = GetEntityType<PanelInbound>(context);
        Assert.Equal("panel_inbounds", inbound.GetTableName());
        Assert.Equal("is_default_for_new_clients",
            inbound.FindProperty(nameof(PanelInbound.IsDefaultForNewClients))!.GetColumnName());
        Assert.Equal("timestamptz",
            inbound.FindProperty(nameof(PanelInbound.LastSyncedAt))!.GetColumnType());

        var clientInbound = GetEntityType<PanelClientInbound>(context);
        Assert.Equal("panel_client_inbounds", clientInbound.GetTableName());

        var syncState = GetEntityType<PanelSyncState>(context);
        Assert.Equal("panel_sync_state", syncState.GetTableName());
        Assert.Equal("cursor_panel_updated_at",
            syncState.FindProperty(nameof(PanelSyncState.CursorPanelUpdatedAt))!.GetColumnName());

        var conflict = GetEntityType<PanelSyncConflict>(context);
        Assert.Equal("panel_sync_conflicts", conflict.GetTableName());
        Assert.Equal("jsonb",
            conflict.FindProperty(nameof(PanelSyncConflict.Details))!.GetColumnType());
        Assert.Equal("timestamptz",
            conflict.FindProperty(nameof(PanelSyncConflict.LastSeenAt))!.GetColumnType());
    }

    [Fact]
    public void Panel_client_keys_and_indexes_are_configured()
    {
        using var context = CreateContext();

        var client = GetEntityType<PanelClient>(context);

        var panelClientKey = client.GetKeys()
            .Single(key => key.Properties.Single().Name == nameof(PanelClient.PanelClientId));
        Assert.Single(panelClientKey.Properties);

        var emailIndex = client.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_panel_clients_email_normalized");
        Assert.True(emailIndex.IsUnique);

        var telegramIndex = client.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_panel_clients_telegram_id");
        Assert.True(telegramIndex.IsUnique);
        Assert.Equal("telegram_id IS NOT NULL", telegramIndex.GetFilter());

        var panelUpdatedIndex = client.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ix_panel_clients_panel_updated_at");
        Assert.Equal(new[] { nameof(PanelClient.PanelUpdatedAt) },
            panelUpdatedIndex.Properties.Select(property => property.Name));

        var deletedIndex = client.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ix_panel_clients_is_deleted_last_synced_at");
        Assert.Equal(
            new[] { nameof(PanelClient.IsDeleted), nameof(PanelClient.LastSyncedAt) },
            deletedIndex.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Panel_relationships_and_composite_key_are_configured()
    {
        using var context = CreateContext();

        var appUserForeignKey = GetEntityType<AppUser>(context).GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(AppUser.PanelClientId));
        Assert.Equal(typeof(PanelClient), appUserForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(PanelClient.PanelClientId),
            appUserForeignKey.PrincipalKey.Properties.Single().Name);
        Assert.Equal(DeleteBehavior.Restrict, appUserForeignKey.DeleteBehavior);

        var loginCodeForeignKey = GetEntityType<EmailLoginCode>(context).GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(EmailLoginCode.PanelClientId));
        Assert.Equal(typeof(PanelClient), loginCodeForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(PanelClient.PanelClientId),
            loginCodeForeignKey.PrincipalKey.Properties.Single().Name);
        Assert.Equal(DeleteBehavior.Cascade, loginCodeForeignKey.DeleteBehavior);

        var clientInbound = GetEntityType<PanelClientInbound>(context);
        Assert.Equal(
            new[] { nameof(PanelClientInbound.PanelClientId), nameof(PanelClientInbound.InboundId) },
            clientInbound.FindPrimaryKey()!.Properties.Select(property => property.Name));

        var clientForeignKey = clientInbound.GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(PanelClient));
        Assert.Equal(DeleteBehavior.Cascade, clientForeignKey.DeleteBehavior);

        var inboundForeignKey = clientInbound.GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(PanelInbound));
        Assert.Equal(DeleteBehavior.NoAction, inboundForeignKey.DeleteBehavior);

        var conflictForeignKey = GetEntityType<PanelSyncConflict>(context).GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(PanelSyncConflict.AppUserId));
        Assert.Equal(typeof(AppUser), conflictForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.SetNull, conflictForeignKey.DeleteBehavior);
    }

    [Fact]
    public void Panel_sync_constraints_and_conflict_indexes_are_configured()
    {
        using var context = CreateContext();

        var syncStateConstraints = GetEntityType<PanelSyncState>(context)
            .GetCheckConstraints()
            .Select(constraint => constraint.Name)
            .ToHashSet();
        Assert.Contains("ck_panel_sync_state_last_run_mode", syncStateConstraints);
        Assert.Contains("ck_panel_sync_state_counts_non_negative", syncStateConstraints);

        var conflict = GetEntityType<PanelSyncConflict>(context);
        Assert.Contains("ck_panel_sync_conflicts_status",
            conflict.GetCheckConstraints().Select(constraint => constraint.Name));

        var dedupeIndex = conflict.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_panel_sync_conflicts_dedupe_key");
        Assert.True(dedupeIndex.IsUnique);

        var statusIndex = conflict.GetIndexes()
            .Single(index => index.GetDatabaseName() ==
                "ix_panel_sync_conflicts_status_last_seen_at");
        Assert.Equal(
            new[] { nameof(PanelSyncConflict.Status), nameof(PanelSyncConflict.LastSeenAt) },
            statusIndex.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Panel_mutable_aggregates_use_xmin_as_a_concurrency_token()
    {
        using var context = CreateContext();

        AssertConcurrencyToken<PanelClient>(context);
        AssertConcurrencyToken<PanelInbound>(context);
        AssertConcurrencyToken<PanelSyncState>(context);
        AssertConcurrencyToken<PanelSyncConflict>(context);

        Assert.Null(GetEntityType<PanelClientInbound>(context).FindProperty("xmin"));
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=eagletunnelapi;Username=postgres;Password=postgres")
            .Options;

        return new AppDbContext(options);
    }

    private static IEntityType GetEntityType<TEntity>(AppDbContext context)
        where TEntity : class
    {
        var model = context.GetService<IDesignTimeModel>().Model;

        return model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the model.");
    }

    private static void AssertConcurrencyToken<TEntity>(AppDbContext context)
        where TEntity : class
    {
        var xmin = GetEntityType<TEntity>(context).FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.True(xmin.IsConcurrencyToken);
    }
}
