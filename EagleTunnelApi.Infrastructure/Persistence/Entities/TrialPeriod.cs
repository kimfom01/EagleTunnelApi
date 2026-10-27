namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class TrialPeriod
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
