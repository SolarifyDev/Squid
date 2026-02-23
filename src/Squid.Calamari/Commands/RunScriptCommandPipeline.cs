using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands;

internal sealed class RunScriptCommandContext : IPathBasedExecutionContext, IVariableLoadingExecutionContext, ITemporaryFileTrackingExecutionContext
{
    public required string ScriptPath { get; init; }

    public required string VariablesPath { get; init; }

    public string? SensitivePath { get; init; }

    public string? Password { get; init; }

    public string InputPath => ScriptPath;

    public string? WorkingDirectory { get; set; }

    public VariableSet? Variables { get; set; }

    public string? BootstrappedScriptPath { get; set; }

    public ScriptExecutionResult? ScriptResult { get; set; }

    public CommandExecutionResult? CommandResult { get; set; }

    public ICollection<string> TemporaryFiles { get; } = new List<string>();
}

internal sealed class WriteBootstrappedBashScriptStep : ExecutionStep<RunScriptCommandContext>
{
    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException("Working directory has not been initialized.");
        if (context.Variables == null)
            throw new InvalidOperationException("Variables have not been loaded.");

        var originalScript = File.ReadAllText(context.ScriptPath);
        var preamble = VariableBootstrapper.GeneratePreamble(context.Variables);
        var bootstrappedScript = preamble + originalScript;

        var bootstrappedPath = Path.Combine(
            context.WorkingDirectory,
            $".squid-bootstrapped-{Guid.NewGuid():N}.sh");
        File.WriteAllText(bootstrappedPath, bootstrappedScript);

        context.BootstrappedScriptPath = bootstrappedPath;
        context.TemporaryFiles.Add(bootstrappedPath);
        return Task.CompletedTask;
    }
}

internal sealed class ExecuteScriptWithEngineStep : ExecutionStep<RunScriptCommandContext>
{
    private readonly IScriptEngine _scriptEngine;

    public ExecuteScriptWithEngineStep(IScriptEngine scriptEngine)
    {
        _scriptEngine = scriptEngine;
    }

    public override async Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException("Working directory has not been initialized.");
        if (string.IsNullOrEmpty(context.BootstrappedScriptPath))
            throw new InvalidOperationException("Bootstrapped script path has not been initialized.");

        var outputProcessor = new ScriptOutputProcessor();
        context.ScriptResult = await _scriptEngine.ExecuteAsync(
                new ScriptExecutionRequest
                {
                    ScriptPath = context.BootstrappedScriptPath,
                    WorkingDirectory = context.WorkingDirectory,
                    Syntax = ScriptSyntax.Bash,
                    OutputProcessor = outputProcessor
                },
                ct)
            .ConfigureAwait(false);
    }
}

internal sealed class BuildRunScriptCommandResultStep : ExecutionStep<RunScriptCommandContext>
{
    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (context.ScriptResult == null)
            throw new InvalidOperationException("Script result has not been produced.");

        context.CommandResult = new CommandExecutionResult(
            context.ScriptResult.ExitCode,
            context.ScriptResult.OutputVariables);

        return Task.CompletedTask;
    }
}
