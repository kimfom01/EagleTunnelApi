namespace EagleTunnelApi.Telegram;

/// <summary>
/// Remembers recently processed Telegram update ids so a webhook redelivery is
/// acknowledged without running the same action twice. Telegram update ids are
/// monotonically increasing; a bounded window keeps memory flat.
/// </summary>
public sealed class TelegramUpdateDeduplicator
{
    private const int Capacity = 1024;

    private readonly object _sync = new();
    private readonly HashSet<int> _seen = [];
    private readonly Queue<int> _order = [];

    /// <summary>
    /// Returns true when the update should be processed. Non-positive ids are
    /// treated as processable because they only occur in tests or malformed
    /// payloads, never in real Telegram updates.
    /// </summary>
    public bool TryRegister(int updateId)
    {
        if (updateId <= 0) return true;

        lock (_sync)
        {
            if (!_seen.Add(updateId)) return false;

            _order.Enqueue(updateId);

            while (_order.Count > Capacity) _seen.Remove(_order.Dequeue());

            return true;
        }
    }
}
