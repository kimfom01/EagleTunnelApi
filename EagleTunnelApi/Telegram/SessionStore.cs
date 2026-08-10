using System.Collections.Concurrent;

namespace EagleTunnelApi.Telegram;

public enum RegistrationStep
{
    None,
    FirstName,
    MiddleName,
    LastName
}

public sealed class UserSession
{
    public RegistrationStep RegistrationStep { get; set; } = RegistrationStep.None;

    public string? FirstName { get; set; }

    public string? MiddleName { get; set; }

    public string? LastName { get; set; }

    public string? SubscriptionUrl { get; set; }

    public SubscriptionStatus? UserStatus { get; set; }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();

    public UserSession Get(long telegramId) => _sessions.GetOrAdd(telegramId, _ => new UserSession());

    public void Reset(long telegramId) => _sessions[telegramId] = new UserSession();
}
