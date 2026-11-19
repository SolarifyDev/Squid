using System.Linq.Expressions;
using Hangfire.Storage;
using Squid.Core.Services.Jobs;

namespace Squid.IntegrationTests.Base;

public class MockSquidBackgroundJobClient : ISquidBackgroundJobClient
{
    public string Enqueue<T>(Expression<Action> methodCall, string queue = "default") => string.Empty;

    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = "default") => string.Empty;

    public string Enqueue(Expression<Func<Task>> methodCall, string queue = "default") => string.Empty;

    /// <summary>Jobs queued through <see cref="Enqueue{T}(Expression{Func{T, Task}}, string)"/>, so a test can run one itself.</summary>
    public List<LambdaExpression> EnqueuedJobs { get; } = new();

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = "default")
    {
        EnqueuedJobs.Add(methodCall);

        return string.Empty;
    }

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = "default") => string.Empty;

    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default") => string.Empty;

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = "default") => string.Empty;

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default") => string.Empty;

    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = "default") => string.Empty;

    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = "default") => string.Empty;

    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo timeZone = null, string queue = "default") { }

    public bool DeleteJob(string jobId) => true;

    public void RemoveRecurringJobIfExists(string jobId) { }

    public List<RecurringJobDto> GetRecurringJobs() => new();

    public StateData GetJobState(string jobId) => null;
}
