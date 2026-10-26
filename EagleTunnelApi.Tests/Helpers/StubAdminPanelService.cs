using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Telegram;

namespace EagleTunnelApi.Tests.Helpers;

/// <summary>
/// Minimal <see cref="IAdminPanelService"/> stub for tests that only exercise
/// referral settlement coordination.
/// </summary>
public sealed class StubAdminPanelService : IAdminPanelService
{
    public Func<CancellationToken, Task<int>> SettleHandler { get; set; } = _ => Task.FromResult(0);

    public int SettleCalls { get; private set; }

    public Task<int> SettlePendingReferralBonusesAsync(CancellationToken cancellationToken)
    {
        SettleCalls++;

        return SettleHandler(cancellationToken);
    }

    public Task<PanelClient?> GetByTargetAsync(string target, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<PanelClientSummary>> ListClientsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<bool> GrantAsync(string email, int days, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task BanAsync(string email, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task UnbanAsync(string email, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task SetDeviceLimitAsync(string email, int limit, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task ResetTrafficAsync(string email, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task LinkToTelegramAsync(string email, long telegramId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task ChangeEmailAsync(string oldEmail, string newEmail, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<bool> TrySettleReferralBonusAsync(long refereeTgId, PanelClient? refereeBefore,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<bool> TrySettleReferralBonusForEmailAsync(string refereeEmail, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
