namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Rebuilds app-owned state from the legacy panel comment tags. The panel
/// mirror is the input; this service never calls the panel and is safe to run
/// repeatedly.
/// </summary>
public interface ICommentBackfill
{
    Task<CommentBackfillReport> RunAsync(CancellationToken cancellationToken);
}

public sealed record CommentBackfillReport(
    int ClientsScanned,
    int ReferralsInserted,
    int ReferralsUpdated,
    int ReferralsSkipped,
    int TrialsInserted,
    int TrialsUpdated,
    int CancellationsInserted,
    int CancellationsUpdated,
    int CancellationsCleared,
    int RemindersInserted,
    int CreditsInserted,
    int CreditsUpdated,
    IReadOnlyList<string> Problems
);
