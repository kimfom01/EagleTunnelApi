namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class ReminderDelivery
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public int DaysBefore { get; set; }
    public DateTimeOffset SentAt { get; set; }
}
