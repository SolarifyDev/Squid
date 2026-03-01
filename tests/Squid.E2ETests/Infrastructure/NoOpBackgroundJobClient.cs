using System.Linq.Expressions;
using Hangfire.Storage;
using Squid.Core.Services.Jobs;

namespace Squid.E2ETests.Infrastructure;

public class NoOpBackgroundJobClient : ISquidBackgroundJobClient
{
    public string Enqueue<T>(Expression<Action> methodCall, string queue = "default") => null;

    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = "default") => null;

    public string Enqueue(Expression<Func<Task>> methodCall, string queue = "default") => null;

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = "default") => null;

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = "default") => null;

    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default") => null;

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = "default") => null;

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default") => null;

    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = "default") => null;

    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = "default") => null;

    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo timeZone = null, string queue = "default") { }

    public bool DeleteJob(string jobId) => false;

    public void RemoveRecurringJobIfExists(string jobId) { }

    public List<RecurringJobDto> GetRecurringJobs() => new();

    public StateData GetJobState(string jobId) => null;
}
