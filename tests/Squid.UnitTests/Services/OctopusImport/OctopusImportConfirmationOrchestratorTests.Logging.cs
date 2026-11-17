using System.Collections.Concurrent;
using System.Linq;
using Serilog;
using Serilog.Events;
using Squid.UnitTests.Support;

namespace Squid.UnitTests.Services.OctopusImport;

// Swaps the process-wide Serilog logger, so the whole class runs serialised with the other classes that do.
[Collection(GlobalStateSerialisedCollection.Name)]
public partial class OctopusImportConfirmationOrchestratorTests
{
    // A confirmation that fails outside a per-resource step (revalidation, the space lock, the source map, SaveChanges, Commit) is
    // recorded as Failed with only the exception's type name; the exception itself, stack and database error, must reach the log
    // with the session it belongs to, or a failed import cannot be diagnosed.
    [Fact]
    public async Task ConfirmAsync_WhenTransactionFails_LogsTheExceptionWithItsSession()
    {
        var sink = new ConcurrentQueue<LogEvent>();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new QueueSink(sink)).CreateLogger();

        try
        {
            var harness = CreateMinimalHarness(throwAfterAction: true, includeReusedEnvironment: true);

            await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

            var logged = sink.Where(e => e.Level == LogEventLevel.Error && e.Exception != null && e.Properties.TryGetValue("SessionId", out var id) && id.ToString() == harness.Request.SessionId.ToString()).ToList();

            logged.ShouldNotBeEmpty();
            logged.ShouldContain(e => e.MessageTemplate.Text.Contains("rolled back", StringComparison.Ordinal));
        }
        finally
        {
            Log.Logger = original;
        }
    }

    private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
