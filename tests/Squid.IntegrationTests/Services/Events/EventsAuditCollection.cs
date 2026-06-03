using Xunit;

namespace Squid.IntegrationTests.Services.Events;

/// <summary>
/// Serializes the Event-audit integration test classes (EventService, lifecycle handler,
/// document interceptor). They each exercise the audit stream against a real database and
/// the shared test-host infrastructure; running them in one collection makes xUnit execute
/// them sequentially, so concurrent DB setup / shared static fixture state cannot race.
/// </summary>
[CollectionDefinition("EventsAudit")]
public sealed class EventsAuditCollection
{
}
