using Squid.Calamari.Execution;
using Squid.Calamari.Execution.Processes;

namespace Squid.Calamari.Kubernetes;

public sealed class KubectlClient : IKubectlClient
{
    private readonly IProcessRunner _processRunner;

    public KubectlClient()
        : this(new ProcessRunner())
    {
    }

    public KubectlClient(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<CommandExecutionResult> ApplyAsync(
        KubectlApplyRequest request,
        CancellationToken ct)
    {
        var outputProcessor = new ScriptOutputProcessor();
        var arguments = new List<string>
        {
            "apply",
            "-f",
            request.ManifestFilePath
        };

        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            arguments.Add("--namespace");
            arguments.Add(request.Namespace);
        }

        var invocation = new ProcessInvocation(
            executable: "kubectl",
            arguments: arguments,
            workingDirectory: request.WorkingDirectory);

        var result = await _processRunner.ExecuteAsync(invocation, outputProcessor.OutputSink, ct)
            .ConfigureAwait(false);

        return new CommandExecutionResult(result.ExitCode, outputProcessor.OutputVariables);
    }
}
