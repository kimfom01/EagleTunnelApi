using EagleTunnelApi.Application.Panel;

namespace EagleTunnelApi.Tests.Helpers;

/// <summary>
/// Test double for the on-demand panel sync runner.
/// </summary>
public sealed class StubPanelSyncRunner : IPanelSyncRunner
{
    public Func<string, CancellationToken, Task<PanelSyncResult>> Handler { get; set; } =
        (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new PanelSyncResult(false, "cursor", 0, 0, 0, 0, 0, null, now, now, null));
        };

    public List<string> Owners { get; } = [];

    public Task<PanelSyncResult> RunNowAsync(string owner, CancellationToken cancellationToken)
    {
        Owners.Add(owner);

        return Handler(owner, cancellationToken);
    }
}
