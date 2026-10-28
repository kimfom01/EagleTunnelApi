using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

public class IdentityModelTests
{
    [Fact]
    public void Identity_tables_and_columns_are_mapped()
    {
        using var context = CreateContext();

        var user = GetEntityType<AppUser>(context);
        Assert.Equal("app_users", user.GetTableName());
        Assert.Equal("email_normalized",
            user.FindProperty(nameof(AppUser.EmailNormalized))!.GetColumnName());
        Assert.Equal("panel_client_id",
            user.FindProperty(nameof(AppUser.PanelClientId))!.GetColumnName());
        Assert.Equal("timestamptz",
            user.FindProperty(nameof(AppUser.CreatedAt))!.GetColumnType());

        var code = GetEntityType<EmailLoginCode>(context);
        Assert.Equal("email_login_codes", code.GetTableName());
        Assert.Equal("bytea", code.FindProperty(nameof(EmailLoginCode.CodeHash))!.GetColumnType());
        Assert.Equal("inet", code.FindProperty(nameof(EmailLoginCode.RequestIp))!.GetColumnType());

        var session = GetEntityType<WebSession>(context);
        Assert.Equal("web_sessions", session.GetTableName());
        Assert.Equal("bytea", session.FindProperty(nameof(WebSession.TokenHash))!.GetColumnType());
        Assert.Equal("inet", session.FindProperty(nameof(WebSession.Ip))!.GetColumnType());

        var audit = GetEntityType<AuditEvent>(context);
        Assert.Equal("audit_events", audit.GetTableName());
        Assert.Equal("timestamptz",
            audit.FindProperty(nameof(AuditEvent.OccurredAt))!.GetColumnType());
        Assert.Equal("jsonb", audit.FindProperty(nameof(AuditEvent.Metadata))!.GetColumnType());
        Assert.Equal("inet", audit.FindProperty(nameof(AuditEvent.Ip))!.GetColumnType());

        var outbox = GetEntityType<EmailOutbox>(context);
        Assert.Equal("email_outbox", outbox.GetTableName());
        Assert.Equal("bytea",
            outbox.FindProperty(nameof(EmailOutbox.PayloadProtected))!.GetColumnType());
    }

    [Fact]
    public void Identity_keys_and_partial_indexes_are_configured()
    {
        using var context = CreateContext();

        var user = GetEntityType<AppUser>(context);
        var emailIndex = user.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_app_users_email_normalized");
        Assert.True(emailIndex.IsUnique);
        Assert.Equal(new[] { nameof(AppUser.EmailNormalized) },
            emailIndex.Properties.Select(property => property.Name));

        var telegramIndex = user.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_app_users_telegram_id");
        Assert.True(telegramIndex.IsUnique);
        Assert.Equal("telegram_id IS NOT NULL", telegramIndex.GetFilter());

        var panelClientIndex = user.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_app_users_panel_client_id");
        Assert.True(panelClientIndex.IsUnique);

        var session = GetEntityType<WebSession>(context);
        var tokenIndex = session.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_web_sessions_token_hash");
        Assert.True(tokenIndex.IsUnique);
        Assert.Equal(new[] { nameof(WebSession.TokenHash) },
            tokenIndex.Properties.Select(property => property.Name));

        var outbox = GetEntityType<EmailOutbox>(context);
        var dedupeIndex = outbox.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_email_outbox_pending_dedupe_key");
        Assert.True(dedupeIndex.IsUnique);
        Assert.Equal("dedupe_key IS NOT NULL AND status IN ('pending', 'processing')",
            dedupeIndex.GetFilter());
    }

    [Fact]
    public void Lookup_and_audit_indexes_are_configured()
    {
        using var context = CreateContext();

        var codeIndexes = GetEntityType<EmailLoginCode>(context).GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ToHashSet();
        Assert.Contains("ix_email_login_codes_email_normalized_expires_at", codeIndexes);
        Assert.Contains("ix_email_login_codes_panel_client_id_expires_at", codeIndexes);

        var sessionIndexes = GetEntityType<WebSession>(context).GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ToHashSet();
        Assert.Contains("ix_web_sessions_app_user_id_expires_at", sessionIndexes);

        var auditIndexes = GetEntityType<AuditEvent>(context).GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ToHashSet();
        Assert.Contains("ix_audit_events_occurred_at", auditIndexes);
        Assert.Contains("ix_audit_events_actor_user_id_occurred_at", auditIndexes);
        Assert.Contains("ix_audit_events_event_type_occurred_at", auditIndexes);

        var outboxIndexes = GetEntityType<EmailOutbox>(context).GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ToHashSet();
        Assert.Contains("ix_email_outbox_status_next_attempt_at", outboxIndexes);
        Assert.Contains("ix_email_outbox_created_at", outboxIndexes);
    }

    [Fact]
    public void Relationships_use_the_expected_delete_behavior()
    {
        using var context = CreateContext();

        var codeForeignKey = GetEntityType<EmailLoginCode>(context).GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(EmailLoginCode.AppUserId));
        Assert.Equal(typeof(AppUser), codeForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, codeForeignKey.DeleteBehavior);

        var sessionForeignKey = GetEntityType<WebSession>(context).GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(WebSession.AppUserId));
        Assert.Equal(typeof(AppUser), sessionForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, sessionForeignKey.DeleteBehavior);

        var auditForeignKey = GetEntityType<AuditEvent>(context).GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(AuditEvent.ActorUserId));
        Assert.Equal(typeof(AppUser), auditForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.SetNull, auditForeignKey.DeleteBehavior);
    }

    [Fact]
    public void Check_constraints_express_storage_invariants()
    {
        using var context = CreateContext();

        var codeConstraints = GetEntityType<EmailLoginCode>(context)
            .GetCheckConstraints()
            .Select(constraint => constraint.Name)
            .ToHashSet();
        Assert.Contains("ck_email_login_codes_attempts_non_negative", codeConstraints);

        var outboxConstraints = GetEntityType<EmailOutbox>(context)
            .GetCheckConstraints()
            .Select(constraint => constraint.Name)
            .ToHashSet();
        Assert.Contains("ck_email_outbox_attempts_non_negative", outboxConstraints);
        Assert.Contains("ck_email_outbox_status", outboxConstraints);
    }

    [Fact]
    public void Mutable_aggregates_use_xmin_as_a_concurrency_token()
    {
        using var context = CreateContext();

        AssertConcurrencyToken<AppUser>(context);
        AssertConcurrencyToken<EmailLoginCode>(context);
        AssertConcurrencyToken<WebSession>(context);
        AssertConcurrencyToken<EmailOutbox>(context);

        Assert.Null(GetEntityType<AuditEvent>(context).FindProperty("xmin"));
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
