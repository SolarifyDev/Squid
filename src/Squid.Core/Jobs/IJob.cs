namespace Squid.Core.Jobs;

public interface IJob : IScopedDependency
{
    Task Execute();
    
    string JobId { get; }
}