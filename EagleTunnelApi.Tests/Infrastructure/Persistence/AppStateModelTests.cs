using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

public class AppStateModelTests
{
    [Fact]
    public void App_state_tables_and_columns_are_mapped()
    {
        using var context = CreateContext();

        var referral = GetEntityType<ReferralAttribution>(context);
        Assert.Equal("referral_attributions", referral.GetTableName());
        Assert.Equal("idempotency_key",
            referral.FindProperty(nameof(ReferralAttribution.IdempotencyKey))!.GetColumnName());
        Assert.Equal("timestamptz",
            referral.FindProperty(nameof(ReferralAttribution.CreatedAt))!.GetColumnType());

        var trial = GetEntityType<TrialPeriod>(context);
        Assert.Equal("trial_periods", trial.GetTableName());
        Assert.Equal("timestamptz",
            trial.FindProperty(nameof(TrialPeriod.EndsAt))!.GetColumnType());

        var cancellation = GetEntityType<CancellationState>(context);
        Assert.Equal("cancellation_state", cancellation.GetTableName());
        Assert.Equal("date",
            cancellation.FindProperty(nameof(CancellationState.ExpiresOn))!.GetColumnType());

        var reminder = GetEntityType<ReminderDelivery>(context);
        Assert.Equal("reminder_deliveries", reminder.GetTableName());
        Assert.Equal("date",
            reminder.FindProperty(nameof(ReminderDelivery.ExpiryDate))!.GetColumnType());

        var credit = GetEntityType<CreditEntry>(context);
        Assert.Equal("credit_entries", credit.GetTableName());
        Assert.Equal("numeric(18,4)",
            credit.FindProperty(nameof(CreditEntry.Amount))!.GetColumnType());
        Assert.Equal("character(3)",
            credit.FindProperty(nameof(CreditEntry.Currency))!.GetColumnType());
        Assert.Equal("jsonb",
            credit.FindProperty(nameof(CreditEntry.Metadata))!.GetColumnType());

        var application = GetEntityType<CreditApplication>(context);
        Assert.Equal("credit_applications", application.GetTableName());
        Assert.Equal("timestamptz",
            application.FindProperty(nameof(CreditApplication.PanelExpiryAfter))!.GetColumnType());
    }

    [Fact]
    public void App_state_check_constraints_are_configured()
    {
        using var context = CreateContext();

        var referralConstraints = GetEntityType<ReferralAttribution>(context)
            .GetCheckConstraints()
            .Select(constraint => constraint.Name)
            .ToHashSet();
        Assert.Contains("ck_referral_attributions_status", referralConstraints);
        Assert.Contains("ck_referral_attributions_source", referralConstraints);

        Assert.Contains("ck_trial_periods_period_valid",
            GetEntityType<TrialPeriod>(context)
                .GetCheckConstraints()
                .Select(constraint => constraint.Name));

        Assert.Contains("ck_reminder_deliveries_days_before_non_negative",
            GetEntityType<ReminderDelivery>(context)
                .GetCheckConstraints()
                .Select(constraint => constraint.Name));

        var creditConstraints = GetEntityType<CreditEntry>(context)
            .GetCheckConstraints()
            .Select(constraint => constraint.Name)
            .ToHashSet();
        Assert.Contains("ck_credit_entries_unit", creditConstraints);
        Assert.Contains("ck_credit_entries_currency", creditConstraints);
        Assert.Contains("ck_credit_entries_amount_non_zero", creditConstraints);
    }

    [Fact]
    public void App_state_keys_and_indexes_are_configured()
    {
        using var context = CreateContext();

        var referralIndex = GetEntityType<ReferralAttribution>(context).GetIndexes()
            .Single(index => index.GetDatabaseName() ==
                "ux_referral_attributions_idempotency_key");
        Assert.True(referralIndex.IsUnique);
        Assert.Equal(new[] { nameof(ReferralAttribution.IdempotencyKey) },
            referralIndex.Properties.Select(property => property.Name));

        var reminderIndex = GetEntityType<ReminderDelivery>(context).GetIndexes()
            .Single(index => index.GetDatabaseName() ==
                "ux_reminder_deliveries_user_expiry_days");
        Assert.True(reminderIndex.IsUnique);
        Assert.Equal(
            new[]
            {
                nameof(ReminderDelivery.UserId),
                nameof(ReminderDelivery.ExpiryDate),
                nameof(ReminderDelivery.DaysBefore)
            },
            reminderIndex.Properties.Select(property => property.Name));

        var credit = GetEntityType<CreditEntry>(context);
        var idempotencyIndex = credit.GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_credit_entries_idempotency_key");
        Assert.True(idempotencyIndex.IsUnique);

        var lookupIndex = credit.GetIndexes()
            .Single(index => index.GetDatabaseName() ==
                "ix_credit_entries_user_id_unit_occurred_at");
        Assert.Equal(
            new[] { nameof(CreditEntry.UserId), nameof(CreditEntry.Unit), nameof(CreditEntry.OccurredAt) },
            lookupIndex.Properties.Select(property => property.Name));

        var applicationIndex = GetEntityType<CreditApplication>(context).GetIndexes()
            .Single(index => index.GetDatabaseName() == "ux_credit_applications_entry_id");
        Assert.True(applicationIndex.IsUnique);

        var cancellationKey = GetEntityType<CancellationState>(context).FindPrimaryKey();
        Assert.NotNull(cancellationKey);
        Assert.Equal(new[] { nameof(CancellationState.UserId) },
            cancellationKey.Properties.Select(property => property.Name));
    }

    [Fact]
    public void App_state_relationships_are_configured()
    {
        using var context = CreateContext();

        var referralForeignKeys = GetEntityType<ReferralAttribution>(context).GetForeignKeys()
            .ToDictionary(key => key.Properties.Single().Name);
        Assert.Equal(typeof(AppUser),
            referralForeignKeys[nameof(ReferralAttribution.ReferrerUserId)]
                .PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict,
            referralForeignKeys[nameof(ReferralAttribution.ReferrerUserId)].DeleteBehavior);
        Assert.Equal(typeof(AppUser),
            referralForeignKeys[nameof(ReferralAttribution.RefereeUserId)]
                .PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict,
            referralForeignKeys[nameof(ReferralAttribution.RefereeUserId)].DeleteBehavior);

        var trialForeignKey = GetEntityType<TrialPeriod>(context).GetForeignKeys().Single();
        Assert.Equal(typeof(AppUser), trialForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, trialForeignKey.DeleteBehavior);

        var cancellationForeignKey = GetEntityType<CancellationState>(context).GetForeignKeys().Single();
        Assert.Equal(typeof(AppUser), cancellationForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, cancellationForeignKey.DeleteBehavior);

        var reminderForeignKey = GetEntityType<ReminderDelivery>(context).GetForeignKeys().Single();
        Assert.Equal(typeof(AppUser), reminderForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, reminderForeignKey.DeleteBehavior);

        var creditForeignKey = GetEntityType<CreditEntry>(context).GetForeignKeys().Single();
        Assert.Equal(typeof(AppUser), creditForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, creditForeignKey.DeleteBehavior);

        var applicationForeignKeys = GetEntityType<CreditApplication>(context).GetForeignKeys()
            .ToDictionary(key => key.Properties.Single().Name);
        Assert.Equal(DeleteBehavior.Restrict,
            applicationForeignKeys[nameof(CreditApplication.UserId)].DeleteBehavior);
        Assert.Equal(typeof(CreditEntry),
            applicationForeignKeys[nameof(CreditApplication.EntryId)].PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict,
            applicationForeignKeys[nameof(CreditApplication.EntryId)].DeleteBehavior);
    }

    [Fact]
    public void App_state_mutable_aggregates_use_xmin_as_a_concurrency_token()
    {
        using var context = CreateContext();

        AssertConcurrencyToken<ReferralAttribution>(context);
        AssertConcurrencyToken<TrialPeriod>(context);
        AssertConcurrencyToken<CancellationState>(context);

        Assert.Null(GetEntityType<ReminderDelivery>(context).FindProperty("xmin"));
        Assert.Null(GetEntityType<CreditEntry>(context).FindProperty("xmin"));
        Assert.Null(GetEntityType<CreditApplication>(context).FindProperty("xmin"));
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
