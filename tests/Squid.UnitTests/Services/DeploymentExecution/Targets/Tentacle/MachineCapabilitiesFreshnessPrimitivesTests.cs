using System;
using System.Collections.Generic;
using Halibut;
using Halibut.Exceptions;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>The small primitives the capability-freshness decisions stand on: the timestamp the cache keeps with each entry, and what counts as "the agent lacks the service".</summary>
public class MachineCapabilitiesFreshnessPrimitivesTests
{
    private static readonly Dictionary<string, string> Meta = new() { ["os"] = "Linux" };

    [Fact]
    public void Store_StampsTheEntryWithTheGivenTime_ElseNow()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var given = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

        cache.Store(1, Meta, "1.0.0", null, given);
        cache.Store(2, Meta, "1.0.0");

        cache.TryGet(1).ObservedAt.ShouldBe(given, "the hydrator and the store-sync pass the persisted timestamp so 'is the shared copy newer' compares like with like");
        cache.TryGet(2).ObservedAt.ShouldNotBeNull().ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void ColdCache_HasNoTimestamp_SoAnyPersistedSnapshotCountsAsNewer()
        => new InMemoryMachineRuntimeCapabilitiesCache().TryGet(99).ObservedAt.ShouldBeNull();

    [Fact]
    public void Hydrator_PassesThePersistedTimestampThrough_ToTheCache()
    {
        var persisted = new MachineRuntimeCapabilities { Os = "Linux", AgentVersion = "1.0.0", SupportedServices = new[] { "IScriptService/v1" }, ObservedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        cache.Store(5, MachineRuntimeCapabilitiesCacheHydrator.BuildMetadataDictionary(persisted), persisted.AgentVersion, persisted.SupportedServices, persisted.ObservedAt);

        cache.TryGet(5).ObservedAt.ShouldBe(persisted.ObservedAt);
        cache.TryGet(5).Os.ShouldBe("Linux");
    }

    [Theory]
    [InlineData(typeof(ServiceNotFoundHalibutClientException), true)]
    [InlineData(typeof(MethodNotFoundHalibutClientException), true)]
    [InlineData(typeof(NoMatchingServiceOrMethodHalibutClientException), true)]
    [InlineData(typeof(HalibutClientException), false)]
    [InlineData(typeof(ServiceInvocationHalibutClientException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void IsServiceMissing_OnlyForTheAgentSayingItHasNoSuchServiceOrMethod(Type type, bool expected)
    {
        var ex = (Exception)Activator.CreateInstance(type, "x");

        TransientFailureClassifier.IsServiceMissing(ex).ShouldBe(expected);
    }
}
