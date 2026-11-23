using System.Collections.Concurrent;
using System.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Squid.UnitTests.Support;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class RedisFallbackLogTests
{
    [Fact]
    public void ManyFallbacks_AreLoggedOncePerMinute_WithTheNumberSkipped()
    {
        var (events, restore) = Capture();

        try
        {
            var clock = new ManualTimeProvider();
            var fallback = new RedisFallbackLog(clock);

            for (var i = 0; i < 50; i++) fallback.Warn(Endpoint, "the Redis breaker is Open");

            events.Count(e => e.Level == LogEventLevel.Warning).ShouldBe(1, "one line, not one per request");

            clock.Advance(TimeSpan.FromSeconds(59));
            fallback.Warn(Endpoint, "again");
            events.Count.ShouldBe(1);

            clock.Advance(TimeSpan.FromSeconds(2));
            fallback.Warn(Endpoint, "again");

            events.Count.ShouldBe(2);
            events.Last().RenderMessage().ShouldContain("50 similar fallbacks", Case.Sensitive);
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void NoMasterKey_StartupCheckWarnsOnce_AndWithOneItStaysQuiet()
    {
        var (events, restore) = Capture();

        try
        {
            new QueueSealingStartupCheck(new Moq.Mock<IQueueMessageKeyProtector>().Object).Check();
            events.ShouldBeEmpty("a protector that wraps is a working master key");

            var broken = new Moq.Mock<IQueueMessageKeyProtector>();

            broken.Setup(p => p.Wrap(It.IsAny<byte[]>())).Throws(new InvalidOperationException("no master key"));

            new QueueSealingStartupCheck(broken.Object).Check();

            events.Count.ShouldBe(1);
            events.Single().Level.ShouldBe(LogEventLevel.Warning);
            events.Single().RenderMessage().ShouldContain("cannot cross server pods");
        }
        finally
        {
            restore();
        }
    }

    private static (ConcurrentQueue<LogEvent> Events, Action Restore) Capture()
    {
        var events = new ConcurrentQueue<LogEvent>();
        var original = Log.Logger;

        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Sink(events)).CreateLogger();

        return (events, () => Log.Logger = original);
    }

    private sealed class Sink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
