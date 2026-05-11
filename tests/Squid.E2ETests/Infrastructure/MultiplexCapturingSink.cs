using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Squid.E2ETests.Infrastructure;

/// <summary>
/// Process-wide multiplex Serilog sink that forwards every event to every
/// currently-registered <see cref="CapturingLogSink"/>. Solves the parallel-
/// fixture race where each fixture used to do
/// <c>Log.Logger = new LoggerConfiguration().Sink(myLogSink).Create()</c>
/// in its own <c>InitializeAsync</c> — the static <c>Log.Logger</c> assignment
/// is last-writer-wins, so concurrent class fixtures clobbered each other's
/// sinks and tests asserting <c>LogSink.ContainsMessage(…)</c> intermittently
/// observed an empty sink even though the production code did emit the log
/// line under the right machine-id / fixture-id.
///
/// <para>Usage: <see cref="E2EFixtureBase{TTestClass}"/> wires a single
/// <c>Log.Logger</c> at process start, with <see cref="Instance"/> as its
/// sink. Each fixture calls <see cref="Register"/> in <c>OnInitializedAsync</c>
/// and <see cref="Unregister"/> in <c>OnDisposingAsync</c>. Multiple sinks
/// can be registered simultaneously; each receives a copy of every event,
/// so per-fixture <c>ContainsMessage</c> checks remain correct (over-
/// reading is harmless — substring matches still hold).</para>
/// </summary>
public sealed class MultiplexCapturingSink : ILogEventSink
{
    public static MultiplexCapturingSink Instance { get; } = new();

    private readonly ConcurrentDictionary<CapturingLogSink, byte> _sinks = new();

    private MultiplexCapturingSink() { }

    public void Register(CapturingLogSink sink) => _sinks.TryAdd(sink, 0);

    public void Unregister(CapturingLogSink sink) => _sinks.TryRemove(sink, out _);

    public void Emit(LogEvent logEvent)
    {
        foreach (var sink in _sinks.Keys)
            sink.Emit(logEvent);
    }
}
