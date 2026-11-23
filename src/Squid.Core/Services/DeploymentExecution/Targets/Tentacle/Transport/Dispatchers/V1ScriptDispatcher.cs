using Halibut;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;

/// <summary>
/// <see cref="IScriptDispatcher"/> for the original (V1) <see cref="IScriptService"/> contract: the one
/// every shipped Tentacle speaks. Builds the <see cref="StartScriptCommand"/> — the positional constructor
/// arguments live ONLY here, and several of them share a type (<c>string</c> mutex name vs correlation
/// task id, <c>TimeSpan</c> timeout vs wait duration), so their order is pinned by
/// <c>HalibutDispatchBehaviourPinningTests</c> — and recognises an unknown ticket by the Tentacle's
/// <c>Complete + UnknownResult (-1)</c> answer.
/// </summary>
public sealed class V1ScriptDispatcher : IScriptDispatcher
{
    private readonly IHalibutClientFactory _halibutClientFactory;

    public V1ScriptDispatcher(IHalibutClientFactory halibutClientFactory)
    {
        _halibutClientFactory = halibutClientFactory;
    }

    public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _halibutClientFactory.CreateClient(endpoint);

    public Func<Task<ScriptStatusResponse>> PrepareStart(IAsyncScriptService client, ScriptDispatchSpec spec)
    {
        var command = BuildCommand(spec);

        return () => client.StartScriptAsync(command);
    }

    public bool IsUnknownTicket(ScriptStatusResponse probe) => IsUnknownTicketProbe(probe);

    /// <summary>
    /// The Tentacle returns <c>Complete + UnknownResult (-1)</c> for an UNKNOWN ticket — the single "agent
    /// doesn't have it" signal — so ONLY that case (or no answer) is unknown. Every other state, including
    /// <see cref="ProcessState.Pending"/>, means the agent holds the ticket. A real script that genuinely
    /// exited -1 collides with the sentinel and is conservatively re-dispatched — an accepted rare edge.
    /// </summary>
    internal static bool IsUnknownTicketProbe(ScriptStatusResponse probe)
        => probe is null || (probe.State == ProcessState.Complete && probe.ExitCode == ScriptExitCodes.UnknownResult);

    private static StartScriptCommand BuildCommand(ScriptDispatchSpec spec)
    {
        return new StartScriptCommand(
            spec.Ticket,
            spec.ScriptBody,
            ScriptIsolationLevel.FullIsolation,
            spec.Timeout,
            spec.IsolationMutexName,
            Array.Empty<string>(),
            spec.ServerTaskId.ToString(),
            TimeSpan.Zero,
            spec.Files)
        {
            ScriptSyntax = MapSyntax(spec.Syntax),
            TargetNamespace = spec.TargetNamespace
        };
    }

    /// <summary>
    /// Translates the server-side script syntax to the Tentacle's wire enum.
    /// All five syntaxes the Tentacle's <c>LocalScriptService</c> can execute
    /// natively are passed through; an unknown syntax is rejected loudly rather
    /// than silently downgraded to <c>Bash</c>, which previously caused Python
    /// scripts to be handed to <c>bash script.sh</c> and crash with
    /// <c>"import: command not found"</c>.
    /// </summary>
    internal static ScriptType MapSyntax(ScriptSyntax syntax)
    {
        return syntax switch
        {
            ScriptSyntax.PowerShell => ScriptType.PowerShell,
            ScriptSyntax.Bash => ScriptType.Bash,
            ScriptSyntax.Python => ScriptType.Python,
            ScriptSyntax.CSharp => ScriptType.CSharp,
            ScriptSyntax.FSharp => ScriptType.FSharp,
            _ => throw new InvalidOperationException($"Unsupported script syntax for Tentacle execution: {syntax}")
        };
    }
}
