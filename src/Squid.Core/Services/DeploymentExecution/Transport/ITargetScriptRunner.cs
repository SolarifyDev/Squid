using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface ITargetScriptRunner : IScopedDependency
{
    Task<ScriptExecutionResult> RunAsync(Machine machine, string scriptBody, ScriptSyntax syntax, CancellationToken ct);
}

public class TargetScriptRunner : ITargetScriptRunner
{
    private readonly Lazy<ITransportRegistry> _transportRegistry;
    private readonly IEndpointContextBuilder _endpointContextBuilder;

    public TargetScriptRunner(Lazy<ITransportRegistry> transportRegistry, IEndpointContextBuilder endpointContextBuilder)
    {
        _transportRegistry = transportRegistry;
        _endpointContextBuilder = endpointContextBuilder;
    }

    public async Task<ScriptExecutionResult> RunAsync(Machine machine, string scriptBody, ScriptSyntax syntax, CancellationToken ct)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);
        var transport = _transportRegistry.Value.Resolve(style);

        if (transport == null)
            return Fail($"No transport for communication style {style}");

        var endpointContext = await _endpointContextBuilder.BuildAsync(machine.Endpoint, transport.Variables, ct).ConfigureAwait(false);
        var variables = transport.Variables.ContributeVariables(endpointContext);

        var scriptContext = new ScriptContext
        {
            Endpoint = endpointContext,
            Syntax = syntax,
            Variables = variables
        };

        var wrappedScript = transport.ScriptWrapper.WrapScript(scriptBody, scriptContext);

        var request = new ScriptExecutionRequest
        {
            Machine = machine,
            ScriptBody = wrappedScript,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            Syntax = syntax,
            Files = new Dictionary<string, byte[]>(),
            Variables = variables,
            EndpointContext = endpointContext
        };

        return await transport.Strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);
    }

    private static ScriptExecutionResult Fail(string message) => new()
    {
        Success = false,
        ExitCode = 1,
        LogLines = new List<string> { message }
    };
}
