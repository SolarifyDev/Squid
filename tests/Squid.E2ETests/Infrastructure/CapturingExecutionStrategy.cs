using Squid.Core.Services.DeploymentExecution;

namespace Squid.E2ETests.Infrastructure;

public class CapturingExecutionStrategy : IExecutionStrategy
{
    public List<ScriptExecutionRequest> CapturedRequests { get; } = new();

    public Func<ScriptExecutionRequest, ScriptExecutionResult> ResultFactory { get; set; }

    public bool CanHandle(string communicationStyle) => true;

    public Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        CapturedRequests.Add(request);

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
        CapturedRequests.Clear();
        ResultFactory = null;
    }
}
