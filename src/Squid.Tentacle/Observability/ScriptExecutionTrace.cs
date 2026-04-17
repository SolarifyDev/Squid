using System.Diagnostics;

namespace Squid.Tentacle.Observability;

/// <summary>
/// Tentacle-side ActivitySource. Parent spans flow in via the W3C
/// traceparent header propagated by the server through
/// <see cref="Squid.Message.Contracts.Tentacle.StartScriptCommand.TraceContext"/>.
/// Child spans on the agent (workspace prep, process launch, complete) are
/// linked automatically so the full trace shows server → Halibut → bash in
/// one waterfall.
/// </summary>
public static class ScriptExecutionTrace
{
    public const string SourceName = "Squid.Tentacle";

    public static readonly ActivitySource Source = new(SourceName, "1.0");

    public const string AttrTicket = "squid.script.ticket";
    public const string AttrScriptType = "squid.script.type";
    public const string AttrIsolation = "squid.script.isolation";
    public const string AttrExitCode = "squid.script.exit_code";
    public const string AttrWorkspacePath = "squid.script.workspace";
    public const string AttrResumed = "squid.script.resumed";

    public static Activity StartScriptExecution(string ticket, string scriptType, string isolation)
    {
        var activity = Source.StartActivity("agent.script.execute", ActivityKind.Server);
        if (activity == null) return null;

        activity.SetTag(AttrTicket, ticket);
        activity.SetTag(AttrScriptType, scriptType);
        activity.SetTag(AttrIsolation, isolation);
        return activity;
    }

    public static Activity StartChild(string name)
        => Source.StartActivity(name, ActivityKind.Internal);

    public static void RecordCompletion(this Activity activity, int exitCode, string workspacePath, bool resumed = false)
    {
        if (activity == null) return;
        activity.SetTag(AttrExitCode, exitCode);
        activity.SetTag(AttrWorkspacePath, workspacePath);
        activity.SetTag(AttrResumed, resumed);
        activity.SetStatus(exitCode == 0 ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }
}
