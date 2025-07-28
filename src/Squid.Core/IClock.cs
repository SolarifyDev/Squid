namespace Squid.Core;

public interface IClock : IScopedDependency
{
    DateTimeOffset Now { get; }

    DateTime DateTimeNow { get; }
}

public class Clock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTime DateTimeNow => DateTime.Now;
}