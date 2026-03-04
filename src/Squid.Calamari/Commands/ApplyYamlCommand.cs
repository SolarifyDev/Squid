using Squid.Calamari.Execution;
using Squid.Calamari.Kubernetes;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands;

/// <summary>
/// Handles the `apply-yaml` subcommand.
/// Loads variables, substitutes #{Variable} tokens in the YAML, then runs kubectl apply.
/// </summary>
public class ApplyYamlCommand
{
    private readonly ExecutionPipeline<ApplyYamlCommandContext> _pipeline;

    public ApplyYamlCommand()
        : this(new RawYamlKubernetesApplyExecutor())
    {
    }

    public ApplyYamlCommand(IKubernetesApplyExecutor kubernetesApplyExecutor)
    {
        _pipeline = new ExecutionPipeline<ApplyYamlCommandContext>(
        [
            new ResolveWorkingDirectoryStep<ApplyYamlCommandContext>(),
            new LoadVariablesFromFilesStep<ApplyYamlCommandContext>(),
            new ExecuteKubernetesApplyStep(kubernetesApplyExecutor),
            new CleanupTemporaryFilesStep<ApplyYamlCommandContext>()
        ]);
    }

    public async Task<int> ExecuteAsync(
        string yamlFile,
        string variablesPath,
        string? sensitivePath,
        string? password,
        string? @namespace,
        CancellationToken ct)
        => (await ExecuteWithResultAsync(yamlFile, variablesPath, sensitivePath, password, @namespace, ct)
            .ConfigureAwait(false)).ExitCode;

    public async Task<CommandExecutionResult> ExecuteWithResultAsync(
        string yamlFile,
        string variablesPath,
        string? sensitivePath,
        string? password,
        string? @namespace,
        CancellationToken ct)
    {
        var context = new ApplyYamlCommandContext
        {
            YamlFilePath = yamlFile,
            VariablesPath = variablesPath,
            SensitivePath = sensitivePath,
            Password = password,
            Namespace = @namespace
        };

        await _pipeline.ExecuteAsync(context, ct).ConfigureAwait(false);

        return context.CommandResult
               ?? throw new InvalidOperationException("Pipeline completed without producing a command result.");
    }
}
