using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Execution.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> ExecuteAsync(
        ProcessInvocation invocation,
        IProcessOutputSink outputSink,
        CancellationToken ct);
}
