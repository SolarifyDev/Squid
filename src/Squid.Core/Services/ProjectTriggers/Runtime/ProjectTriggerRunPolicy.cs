using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

internal static class ProjectTriggerRunPolicy
{
    private const int MaxAttempts = 5;
    internal const int MaxBatchSize = 32;

    internal static int BatchLimit(int requested)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requested);

        return Math.Min(requested, MaxBatchSize);
    }

    internal static TriggerResourceScope ActualScope(TriggerActionPlan plan)
    {
        return new(plan.ChannelId, plan.SourceEnvironmentId, plan.EnvironmentId);
    }

    // A handler may select an existing release from any permitted project channel when its configuration leaves the channel open.
    internal static bool MatchesScope(TriggerActionPlan plan, TriggerResourceScope allowed)
    {
        return allowed != null && (allowed.ChannelId == null || allowed.ChannelId == plan.ChannelId) &&
            allowed.SourceEnvironmentId == plan.SourceEnvironmentId && allowed.DestinationEnvironmentId == plan.EnvironmentId;
    }

    internal static bool SameExecution(ProjectTrigger expected, ProjectTrigger current)
    {
        return expected.Revision >= current.ExecutionRevision && expected.Revision <= current.Revision &&
            expected.ExecutionUserId == current.ExecutionUserId &&
            ProjectTriggerObserver.Definition(expected) == ProjectTriggerObserver.Definition(current);
    }

    internal static string InactiveReason(ProjectTrigger trigger, ProjectTriggerRun run)
    {
        if (trigger == null || trigger.IsDeleted) return "The trigger was deleted.";
        if (trigger.IsDisabled) return "The trigger was disabled.";
        if (trigger.ExecutionUserId != run.ExecutionUserId) return "The trigger execution identity changed.";
        if (run.Revision < trigger.ExecutionRevision || run.Revision > trigger.Revision ||
            trigger.TriggerType != run.TriggerType || trigger.ActionType != run.ActionType ||
            trigger.ConfigurationJson != run.ConfigurationJson) return "The trigger revision changed.";

        return null;
    }

    internal static void RecordFailure(ProjectTriggerRun run, DateTimeOffset now, string reason, bool retryable)
    {
        run.Attempts++;
        run.Reason = reason;

        if (!retryable || run.Attempts >= MaxAttempts)
        {
            run.Status = ProjectTriggerRunStatuses.Failed;
            run.CompletedAt = now;

            if (retryable) run.Reason += " The retry limit was reached.";

            return;
        }

        run.NextAttemptAt = now.AddMinutes(1 << (run.Attempts - 1));
    }
}
