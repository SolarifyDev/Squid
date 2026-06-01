using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut.Diagnostics;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Pins the connection-log reader: it resolves a machine's Halibut endpoint URI,
/// reads the real per-endpoint connection events, projects them to the API DTO
/// (type/message/error/time), returns the most-recent N, and degrades safely
/// (unknown machine, unresolvable endpoint, no events). Uses a fake ILogFactory
/// so the mapping is exercised without a live Halibut runtime — Halibut's ILog
/// interface is read-only (GetLogs), so a fake is the natural seam.
/// </summary>
public sealed class MachineConnectionLogReaderTests
{
    private const string PollingEndpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-xyz","Thumbprint":"AABB"}""";

    private static LogEvent Event(EventType type, string message, Exception error = null)
        => new(type, message, error, Array.Empty<object>());

    private static (MachineConnectionLogReader reader, FakeLogFactory factory) Build(Machine machine)
    {
        var provider = new Mock<IMachineDataProvider>();
        provider.Setup(p => p.GetMachinesByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var factory = new FakeLogFactory();
        return (new MachineConnectionLogReader(provider.Object, factory), factory);
    }

    private static Machine MachineWith(string endpoint) => new() { Id = 5, Name = "agent", Endpoint = endpoint };

    [Fact]
    public async Task ReadAsync_ProjectsEvents_TypeMessageErrorAndEndpoint()
    {
        var (reader, factory) = Build(MachineWith(PollingEndpoint));
        var boom = new InvalidOperationException("handshake rejected");
        factory.Seed("poll://sub-xyz/",
            Event(EventType.OpeningNewConnection, "Opening a new connection"),
            Event(EventType.Error, "Connection failed", boom));

        var data = await reader.ReadAsync(5, maxEntries: 200, CancellationToken.None);

        data.MachineId.ShouldBe(5);
        data.Endpoint.ShouldBe("poll://sub-xyz/");
        data.Entries.Count.ShouldBe(2);

        data.Entries[0].Type.ShouldBe("OpeningNewConnection");
        data.Entries[0].Message.ShouldBe("Opening a new connection");
        data.Entries[0].Error.ShouldBe(string.Empty);

        data.Entries[1].Type.ShouldBe("Error");
        data.Entries[1].Error.ShouldBe("handshake rejected",
            customMessage: "the error's concise message must be surfaced so operators see why a connection failed.");
    }

    [Fact]
    public async Task ReadAsync_CapsToMostRecentEntries_InChronologicalOrder()
    {
        var (reader, factory) = Build(MachineWith(PollingEndpoint));
        factory.Seed("poll://sub-xyz/",
            Event(EventType.MessageExchange, "e1"),
            Event(EventType.MessageExchange, "e2"),
            Event(EventType.MessageExchange, "e3"),
            Event(EventType.MessageExchange, "e4"));

        var data = await reader.ReadAsync(5, maxEntries: 2, CancellationToken.None);

        data.Entries.Select(e => e.Message).ShouldBe(new[] { "e3", "e4" },
            customMessage: "must return the most recent N entries, oldest-to-newest within the cap.");
    }

    [Fact]
    public async Task ReadAsync_ListeningEndpoint_ResolvesHttpsUri()
    {
        var listening = """{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.9:10933/","Thumbprint":"CCDD"}""";
        var (reader, factory) = Build(MachineWith(listening));
        factory.Seed("https://10.0.0.9:10933/", Event(EventType.SecurityNegotiation, "TLS ok"));

        var data = await reader.ReadAsync(5, maxEntries: 200, CancellationToken.None);

        data.Endpoint.ShouldBe("https://10.0.0.9:10933/");
        data.Entries.Single().Type.ShouldBe("SecurityNegotiation");
    }

    [Fact]
    public async Task ReadAsync_NoEventsForEndpoint_ReturnsEndpointWithEmptyEntries()
    {
        var (reader, _) = Build(MachineWith(PollingEndpoint));   // factory seeded with nothing

        var data = await reader.ReadAsync(5, maxEntries: 200, CancellationToken.None);

        data.Endpoint.ShouldBe("poll://sub-xyz/");
        data.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_UnknownMachine_ReturnsEmpty_NoEndpoint()
    {
        var (reader, _) = Build(machine: null);

        var data = await reader.ReadAsync(404, maxEntries: 200, CancellationToken.None);

        data.MachineId.ShouldBe(404);
        data.Endpoint.ShouldBe(string.Empty);
        data.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_UnresolvableEndpoint_ReturnsEmpty_NoEndpoint()
    {
        // Endpoint JSON missing the SubscriptionId/Thumbprint → no URI to key on.
        var (reader, _) = Build(MachineWith("""{"CommunicationStyle":"TentaclePolling"}"""));

        var data = await reader.ReadAsync(5, maxEntries: 200, CancellationToken.None);

        data.Endpoint.ShouldBe(string.Empty);
        data.Entries.ShouldBeEmpty();
    }

    // ── Fakes ────────────────────────────────────────────────────────────────
    // Halibut's ILog is read-only (GetLogs + ForContext); a fake factory keyed by
    // URI lets us seed events without standing up a real HalibutRuntime.

    private sealed class FakeLogFactory : ILogFactory
    {
        private readonly Dictionary<string, FakeLog> _byEndpoint = new(StringComparer.Ordinal);

        public void Seed(string endpoint, params LogEvent[] events)
            => _byEndpoint[endpoint] = new FakeLog(events.ToList());

        public ILog ForEndpoint(Uri endpoint)
            => _byEndpoint.TryGetValue(endpoint.ToString(), out var log) ? log : new FakeLog(new List<LogEvent>());

        public ILog ForPrefix(string prefix) => new FakeLog(new List<LogEvent>());
    }

    private sealed class FakeLog : ILog
    {
        private readonly IList<LogEvent> _events;
        public FakeLog(IList<LogEvent> events) { _events = events; }
        public IList<LogEvent> GetLogs() => _events;
        public ILog ForContext() => this;
        public ILog ForContext<T>() => this;
        public void Write(EventType type, string message, params object[] args) => _events.Add(new LogEvent(type, message, null, args));
        public void WriteException(EventType type, string message, Exception ex, params object[] args) => _events.Add(new LogEvent(type, message, ex, args));
    }
}
