using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.E2ETests.Infrastructure;

public class CapturingExecutionStrategy : IExecutionStrategy
{
    private readonly object _lock = new();
    private readonly List<ScriptExecutionRequest> _capturedRequests = new();

    public List<ScriptExecutionRequest> CapturedRequests => _capturedRequests;

    public Func<ScriptExecutionRequest, ScriptExecutionResult> ResultFactory { get; set; }

    public Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            _capturedRequests.Add(request);
        }

        // ResultFactory may Thread.Sleep to simulate a long-running script. The
        // pipeline's ct fires during that sleep but Thread.Sleep can't observe it.
        // After the factory returns, honour cancellation here so the pipeline sees
        // OperationCanceledException and transitions the task to TaskState.Cancelled
        // (production behaviour: a real strategy checks ct between poll/complete
        // round-trips).
        var result = ResultFactory != null
            ? ResultFactory(request)
            : new ScriptExecutionResult { Success = true, ExitCode = 0 };

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(result);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _capturedRequests.Clear();
        }

        ResultFactory = null;
    }
}
