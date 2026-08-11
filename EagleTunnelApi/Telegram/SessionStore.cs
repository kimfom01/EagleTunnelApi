using System.Collections.Concurrent;

namespace EagleTunnelApi.Telegram;

public sealed class UserSession
{
    public string? SubscriptionUrl { get; set; }

    public SubscriptionStatus? UserStatus { get; set; }

    public string? MainMenuText { get; set; }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();

    public UserSession Get(long telegramId) => _sessions.GetOrAdd(telegramId, _ => new UserSession());

    public void Reset(long telegramId) => _sessions[telegramId] = new UserSession();
}
