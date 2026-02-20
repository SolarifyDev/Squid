using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Squid.E2ETests.Infrastructure;

public class CapturingLogSink : ILogEventSink
{
    public ConcurrentBag<string> Messages { get; } = new();

    public void Emit(LogEvent logEvent)
    {
        Messages.Add(logEvent.RenderMessage());
    }

    public bool ContainsMessage(string substring)
    {
        return Messages.Any(m => m.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        while (Messages.TryTake(out _)) { }
    }
}
