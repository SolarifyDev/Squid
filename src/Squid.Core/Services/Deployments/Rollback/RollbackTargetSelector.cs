namespace Squid.Core.Services.Deployments.Rollback;

/// <summary>
/// PR-12 — pure selection of the rollback target from an environment's
/// successful-deployment history. No DB / I/O — driven entirely by the
/// ordered list the resolver hands it, so the "which release do we roll back
/// to?" rule is unit-testable in isolation.
///
/// <para><b>Rule</b>: "roll back" re-deploys the release that was successfully
/// running BEFORE the current one — the most recent successful deployment
/// whose release differs from the latest (current) release. This is robust to
/// the same release being deployed several times in a row (those collapse to
/// "current") and to a release being re-deployed after a newer one (we still
/// roll back to whatever distinct release preceded the current state).</para>
///
/// <para>Returns <see langword="null"/> when there is no prior distinct
/// release — the environment has never been deployed to, or has only ever run
/// a single release. The caller surfaces that as "nothing to roll back to".</para>
/// </summary>
public static class RollbackTargetSelector
{
    public static RollbackReleaseHistoryEntry? SelectPreviousDistinctRelease(IReadOnlyList<RollbackReleaseHistoryEntry> historyNewestFirst)
    {
        if (historyNewestFirst is null || historyNewestFirst.Count == 0) return null;

        var currentReleaseId = historyNewestFirst[0].ReleaseId;

        foreach (var entry in historyNewestFirst)
            if (entry.ReleaseId != currentReleaseId)
                return entry;

        return null;
    }
}
