using Squid.Calamari.Execution;
using Squid.Calamari.Kubernetes;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands;

internal sealed class ApplyYamlCommandContext : IPathBasedExecutionContext, IVariableLoadingExecutionContext, ITemporaryFileTrackingExecutionContext
{
    public required string YamlFilePath { get; init; }

    public required string VariablesPath { get; init; }

    public string? SensitivePath { get; init; }

    public string? Password { get; init; }

    public string? Namespace { get; init; }

    public string InputPath => YamlFilePath;

    public string? WorkingDirectory { get; set; }

    public VariableSet? Variables { get; set; }

    public CommandExecutionResult? CommandResult { get; set; }

    public ICollection<string> TemporaryFiles { get; } = new List<string>();
}

internal sealed class ExecuteKubernetesApplyStep : ExecutionStep<ApplyYamlCommandContext>
{
    private readonly IKubernetesApplyExecutor _executor;

    public ExecuteKubernetesApplyStep(IKubernetesApplyExecutor executor)
    {
        _executor = executor;
    }

    public override async Task ExecuteAsync(ApplyYamlCommandContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException("Working directory has not been initialized.");
        if (context.Variables == null)
            throw new InvalidOperationException("Variables have not been loaded.");

        context.CommandResult = await _executor.ExecuteAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = context.WorkingDirectory,
                    YamlFilePath = context.YamlFilePath,
                    Variables = context.Variables,
                    Namespace = context.Namespace,
                    TemporaryFiles = context.TemporaryFiles
                },
                ct)
            .ConfigureAwait(false);
    }
}
