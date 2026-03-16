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

        if (ResultFactory != null)
            return Task.FromResult(ResultFactory(request));

        return Task.FromResult(new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0
        });
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
