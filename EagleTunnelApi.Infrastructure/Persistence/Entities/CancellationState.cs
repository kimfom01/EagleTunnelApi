namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class CancellationState
{
    public Guid UserId { get; set; }
    public DateTimeOffset CancelledAt { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
}
