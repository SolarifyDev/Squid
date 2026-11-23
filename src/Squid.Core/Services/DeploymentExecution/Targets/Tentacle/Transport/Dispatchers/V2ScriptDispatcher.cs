using Halibut;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;

/// <summary>
/// <see cref="IScriptDispatcher"/> for the <see cref="IScriptServiceV2"/> contract. Only ever chosen by
/// <see cref="IScriptDispatcherSelector"/> after the agent has advertised
/// <see cref="ScriptServiceCapabilities.V2"/> (or when a recorded script was started with V2): opening the
/// client is not a probe — an agent without V2 answers a V2 call with a plain transport exception that the
/// transient-failure classifier would treat as a blip.
///
/// <para>Builds the <see cref="StartScriptCommandV2"/> by property name — every field set explicitly, never
/// positionally — from the same protocol-neutral <see cref="ScriptDispatchSpec"/> V1 uses, so both versions
/// send the same values (mutex name, correlation task id, timeout, syntax, namespace, files).</para>
/// </summary>
public sealed class V2ScriptDispatcher : IScriptDispatcher
{
    private readonly IScriptServiceV2ClientFactory _clientFactory;

    public V2ScriptDispatcher(IScriptServiceV2ClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public ScriptProtocolVersion Version => ScriptProtocolVersion.V2;

    public IScriptAgentSession Open(ServiceEndPoint endpoint, ScriptSessionContext context)
    {
        var raw = new V2ScriptAgentClient(_clientFactory.CreateScriptServiceV2Client(endpoint));
        var retrying = new RetryingScriptAgentClient(raw, context.Retry, context.ScriptTimeout, context.Ct);

        return new V2ScriptSession(raw, retrying, context);
    }

    internal static StartScriptCommandV2 BuildCommand(ScriptDispatchSpec spec)
    {
        return new StartScriptCommandV2
        {
            ScriptTicket = spec.Ticket,
            ScriptBody = spec.ScriptBody,
            Isolation = ScriptIsolationLevel.FullIsolation,
            ScriptIsolationMutexTimeout = spec.Timeout,
            IsolationMutexName = spec.IsolationMutexName,
            Arguments = Array.Empty<string>(),
            TaskId = spec.ServerTaskId.ToString(),
            DurationToWaitForScriptToFinish = TimeSpan.Zero,
            ScriptSyntax = V1ScriptDispatcher.MapSyntax(spec.Syntax),
            TargetNamespace = spec.TargetNamespace,
            Files = spec.Files.ToList()
        };
    }
}
