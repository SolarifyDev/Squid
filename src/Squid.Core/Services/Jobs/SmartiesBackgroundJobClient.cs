using System.Linq.Expressions;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;

namespace Squid.Core.Services.Jobs;

public interface ISquidBackgroundJobClient : IScopedDependency
{
    string Enqueue<T>(Expression<Action> methodCall, string queue = "default");
    
    string Enqueue<T>(Expression<Action<T>> methodCall, string queue = "default");
    
    string Enqueue(Expression<Func<Task>> methodCall, string queue = "default");
    
    string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = "default");

    string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = "default");
    
    string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default");

    string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = "default");
    
    string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default");
    
    string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = "default");
        
    string ContinueJobWith<T>(string parentJobId, Expression<Func<T,Task>> methodCall, string queue = "default");
    
    void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo timeZone = null, string queue = "default");
    
    bool DeleteJob(string jobId);

    void RemoveRecurringJobIfExists(string jobId);
    
    List<RecurringJobDto> GetRecurringJobs();
    
    StateData GetJobState(string jobId);
}

public class SquidBackgroundJobClient : ISquidBackgroundJobClient
{
    private readonly Func<IBackgroundJobClient> _backgroundJobClientFunc;
    private readonly Func<IRecurringJobManager> _recurringJobManagerFunc;
    
    public SquidBackgroundJobClient(Func<IBackgroundJobClient> backgroundJobClientFunc, Func<IRecurringJobManager> recurringJobManagerFunc)
    {
        _backgroundJobClientFunc = backgroundJobClientFunc;
        _recurringJobManagerFunc = recurringJobManagerFunc;
    }

    public string Enqueue(Expression<Func<Task>> methodCall, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Create(methodCall, new EnqueuedState(queue));
    }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Create(methodCall, new EnqueuedState(queue));
    }
    
    public string Enqueue<T>(Expression<Action> methodCall, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Create(methodCall, new EnqueuedState(queue));
    }
    
    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Create(methodCall, new EnqueuedState(queue));
    }
    
    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Schedule(queue, methodCall, delay);
    }
    
    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Schedule(queue, methodCall, enqueueAt);
    }

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Schedule(queue, methodCall, delay);
    }

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = "default")
    {
        return _backgroundJobClientFunc()?.Schedule(queue, methodCall, enqueueAt);
    }

    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = "default")
    {
        return _backgroundJobClientFunc()?.ContinueJobWith(parentJobId, methodCall, new EnqueuedState(queue));
    }
    
    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = "default")
    {
        return _backgroundJobClientFunc()?.ContinueJobWith(parentJobId, methodCall, new EnqueuedState(queue));
    }

    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo timeZone = null, string queue = "default")
    {
        _recurringJobManagerFunc()?.AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions
        {
            TimeZone = timeZone ?? TimeZoneInfo.Utc
        });
    }

    public bool DeleteJob(string jobId)
    {
        return _backgroundJobClientFunc()?.Delete(jobId) ?? false;
    }

    public void RemoveRecurringJobIfExists(string jobId)
    {
        _recurringJobManagerFunc()?.RemoveIfExists(jobId);
    }

    public List<RecurringJobDto> GetRecurringJobs()
    {
        return JobStorage.Current.GetConnection().GetRecurringJobs();
    }
    
    public StateData GetJobState(string jobId)
    {
        return JobStorage.Current.GetConnection().GetStateData(jobId);
    }
}