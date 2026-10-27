using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace EagleTunnelApi.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();

    public DbSet<EmailLoginCode> EmailLoginCodes => Set<EmailLoginCode>();

    public DbSet<WebSession> WebSessions => Set<WebSession>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();

    public DbSet<PanelClient> PanelClients => Set<PanelClient>();

    public DbSet<PanelInbound> PanelInbounds => Set<PanelInbound>();

    public DbSet<PanelClientInbound> PanelClientInbounds => Set<PanelClientInbound>();

    public DbSet<PanelSyncState> PanelSyncStates => Set<PanelSyncState>();

    public DbSet<PanelSyncConflict> PanelSyncConflicts => Set<PanelSyncConflict>();

    public DbSet<ReferralAttribution> ReferralAttributions => Set<ReferralAttribution>();

    public DbSet<TrialPeriod> TrialPeriods => Set<TrialPeriod>();

    public DbSet<CancellationState> CancellationStates => Set<CancellationState>();

    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();

    public DbSet<CreditEntry> CreditEntries => Set<CreditEntry>();

    public DbSet<CreditApplication> CreditApplications => Set<CreditApplication>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
