using System.Diagnostics;

namespace Squid.Core.Observability;

/// <summary>
/// Central <see cref="ActivitySource"/> for deployment execution. Any component
/// in the pipeline can start spans from here; if an OpenTelemetry collector
/// is registered (OTEL_EXPORTER_OTLP_ENDPOINT env var), the spans flow out to
/// Jaeger / Tempo / Datadog. If no collector is registered, ActivitySource is
/// cheap (≈ a pointer check) — safe to leave spans enabled in production.
///
/// The source name matches the OTel "service name" convention so
/// trace backends group Squid spans under one service automatically.
/// </summary>
public static class DeploymentTracing
{
    public const string SourceName = "Squid.Deployment";
    public const string SourceVersion = "1.0";

    public static readonly ActivitySource Source = new(SourceName, SourceVersion);

    public const string AttrServerTaskId = "squid.deployment.server_task_id";
    public const string AttrDeploymentId = "squid.deployment.id";
    public const string AttrReleaseVersion = "squid.deployment.release_version";
    public const string AttrBatchIndex = "squid.deployment.batch_index";
    public const string AttrStepName = "squid.deployment.step.name";
    public const string AttrStepOrder = "squid.deployment.step.order";
    public const string AttrActionName = "squid.deployment.action.name";
    public const string AttrMachineId = "squid.deployment.machine.id";
    public const string AttrMachineName = "squid.deployment.machine.name";
    public const string AttrCommunicationStyle = "squid.deployment.machine.communication_style";
    public const string AttrScriptTicket = "squid.deployment.script.ticket";
    public const string AttrScriptExitCode = "squid.deployment.script.exit_code";
    public const string AttrScriptFailed = "squid.deployment.script.failed";

    public static Activity StartDeployment(int serverTaskId, int deploymentId, string releaseVersion)
    {
        var activity = Source.StartActivity("deployment.execute", ActivityKind.Internal);
        if (activity == null) return null;

        activity.SetTag(AttrServerTaskId, serverTaskId);
        activity.SetTag(AttrDeploymentId, deploymentId);
        activity.SetTag(AttrReleaseVersion, releaseVersion);
        return activity;
    }

    public static Activity StartBatch(int batchIndex)
    {
        var activity = Source.StartActivity("deployment.batch", ActivityKind.Internal);
        activity?.SetTag(AttrBatchIndex, batchIndex);
        return activity;
    }

    public static Activity StartStep(string stepName, int stepOrder)
    {
        var activity = Source.StartActivity("deployment.step", ActivityKind.Internal);
        if (activity == null) return null;

        activity.SetTag(AttrStepName, stepName);
        activity.SetTag(AttrStepOrder, stepOrder);
        return activity;
    }

    public static Activity StartTargetExecution(string stepName, string actionName, int machineId, string machineName, string communicationStyle)
    {
        var activity = Source.StartActivity("deployment.target.execute", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag(AttrStepName, stepName);
        activity.SetTag(AttrActionName, actionName);
        activity.SetTag(AttrMachineId, machineId);
        activity.SetTag(AttrMachineName, machineName);
        activity.SetTag(AttrCommunicationStyle, communicationStyle);
        return activity;
    }

    public static void RecordScriptResult(this Activity activity, string ticket, int exitCode, bool failed)
    {
        if (activity == null) return;
        activity.SetTag(AttrScriptTicket, ticket);
        activity.SetTag(AttrScriptExitCode, exitCode);
        activity.SetTag(AttrScriptFailed, failed);
        activity.SetStatus(failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
    }

    public static void RecordException(this Activity activity, Exception exception)
    {
        if (activity == null || exception == null) return;
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddTag("exception.type", exception.GetType().FullName);
        activity.AddTag("exception.message", exception.Message);
    }
}
