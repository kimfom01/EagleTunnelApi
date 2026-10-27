namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class CreditApplication
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EntryId { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public DateTimeOffset PanelExpiryBefore { get; set; }
    public DateTimeOffset PanelExpiryAfter { get; set; }
}
