using System.Net;

namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class EmailLoginCode
{
    public Guid Id { get; set; }
    public long PanelClientId { get; set; }
    public Guid? AppUserId { get; set; }
    public string EmailNormalized { get; set; } = string.Empty;
    public byte[] CodeHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public int Attempts { get; set; }
    public IPAddress? RequestIp { get; set; }
}
