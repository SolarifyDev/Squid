using Squid.Calamari.Execution.Output;
using Squid.Calamari.Execution.Processes;

namespace Squid.Calamari.Execution;

/// <summary>
/// Executes a bash script file, streaming stdout/stderr through ScriptOutputProcessor.
/// </summary>
public class BashScriptExecutor
{
    private readonly IProcessRunner _processRunner;

    public BashScriptExecutor()
        : this(new ProcessRunner())
    {
    }

    public BashScriptExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<int> ExecuteAsync(
        string scriptPath,
        string workDir,
        ScriptOutputProcessor outputProcessor,
        CancellationToken ct)
    {
        var invocation = new ProcessInvocation(
            executable: "bash",
            arguments: [scriptPath],
            workingDirectory: workDir);

        var result = await _processRunner.ExecuteAsync(invocation, outputProcessor.OutputSink, ct)
            .ConfigureAwait(false);

        return result.ExitCode;
    }
}
