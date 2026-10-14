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
    LinkTelegramId,
    NudgeTarget,
    RegisterTarget,
    RegisterReferrer,
    RegisterDays,
    ChangeEmailTarget,
    ChangeEmailValue
}

public enum RegistrationStep
{
    None,
    AwaitingEmail,
    AwaitingReferrer
}

public sealed class UserSession
{
    public string? SubscriptionUrl { get; set; }

    public SubscriptionStatus? UserStatus { get; set; }

    public string? MainMenuText { get; set; }

    public AdminAction AdminAction { get; set; } = AdminAction.None;

    public string? AdminTargetEmail { get; set; }

    public long? AdminReferrerTgId { get; set; }

    public string? AdminReferrerEmail { get; set; }

    public List<string>? AdminCandidates { get; set; }

    public AdminAction AdminCandidatesAction { get; set; } = AdminAction.None;

    public RegistrationStep RegistrationStep { get; set; } = RegistrationStep.None;

    public string? PendingEmail { get; set; }

    public long? PendingReferrerTgId { get; set; }

    public string? PendingReferrerEmail { get; set; }

    public bool CollectReferrer { get; set; }

    public bool ReferrerRequired { get; set; }

    public bool FriendInviteActive { get; set; }

    public bool ReturnToReferral { get; set; }

    /// <summary>
    /// Email of the most recent friend invite, so its QR code can be re-sent
    /// without stuffing the address into callback data (64-byte limit).
    /// </summary>
    public string? LastInviteEmail { get; set; }

    /// <summary>
    /// Resolved subscription URL of the most recent friend invite.
    /// </summary>
    public string? LastInviteSubscriptionUrl { get; set; }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();

    public UserSession Get(long telegramId)
    {
        return _sessions.GetOrAdd(telegramId, _ => new UserSession());
    }

    public void Reset(long telegramId)
    {
        _sessions[telegramId] = new UserSession();
    }
}