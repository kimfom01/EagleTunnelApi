namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// One-time, repeatable initial population of the panel mirror and the legacy
/// comment-held state. It pages the panel list, refreshes each row to a full
/// client, applies the mirror writer, and then runs the comment backfill.
/// </summary>
public interface IPanelBackfill
{
    Task<PanelBackfillReport> RunAsync(CancellationToken cancellationToken);
}

public sealed record PanelBackfillReport(
    int PanelRowsScanned,
    int MirrorApplied,
    int MirrorConflicted,
    int DetailLookupFailures,
    IReadOnlyList<string> Problems,
    CommentBackfillReport Comments
);
