using System.Collections.Concurrent;

namespace EagleTunnelApi.Telegram;

public enum AdminAction
{
    None,
    Lookup,
    GrantTarget,
    GrantDays,
    BanTarget,
    UnbanTarget,
    LimitTarget,
    LimitValue,
    ResetTrafficTarget,
    LinkTarget,
    LinkTelegramId
}

public sealed class UserSession
{
    public string? SubscriptionUrl { get; set; }

    public SubscriptionStatus? UserStatus { get; set; }

    public string? MainMenuText { get; set; }

    public AdminAction AdminAction { get; set; } = AdminAction.None;

    public string? AdminTargetEmail { get; set; }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();

    public UserSession Get(long telegramId) => _sessions.GetOrAdd(telegramId, _ => new UserSession());

    public void Reset(long telegramId) => _sessions[telegramId] = new UserSession();
}
