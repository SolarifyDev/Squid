using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Halibut;
using Squid.Core.Halibut;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Which dispatcher serves a machine is a pure function of what the agent ADVERTISED. The matrix below is
/// the contract: V2 only when the cached capabilities list it; everything else — cold cache, V1-only
/// agents, the list synthesised for agents that predate the capabilities service, no cache at all — is V1,
/// which every agent speaks. No case is decided by calling an agent and catching a failure: on Halibut a
/// V1-only agent answers a V2 call with a plain transport exception that the classifier treats as transient.
/// </summary>
public class ScriptDispatcherSelectorTests
{
    private const int MachineId = 42;

    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();
    private readonly CountingV2Factory _v2Factory = new();
    private readonly IScriptDispatcher _v1 = new V1ScriptDispatcher(new Moq.Mock<IHalibutClientFactory>().Object);

    private ScriptDispatcherSelector Selector(bool withV2 = true, bool withCache = true)
    {
        var dispatchers = new List<IScriptDispatcher> { _v1 };
        if (withV2) dispatchers.Add(new V2ScriptDispatcher(_v2Factory));

        return new ScriptDispatcherSelector(dispatchers, withCache ? _cache : null);
    }

    private void Advertise(params string[] services)
        => _cache.Store(MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "1.0.0", services);

    [Fact]
    public void ColdCache_IsV1()
        => Selector().ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V1);

    [Fact]
    public void V1OnlyAgent_IsV1()
    {
        Advertise("IScriptService/v1", "ICapabilitiesService/v1");

        Selector().ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V1);
    }

    [Fact]
    public void AgentAdvertisingV1AndV2_IsV2()
    {
        Advertise("IScriptService/v1", ScriptServiceCapabilities.V2);

        Selector().ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V2);
    }

    [Fact]
    public void AdvertisedV2_MatchesCaseInsensitively_AsTheCacheDoes()
    {
        Advertise("iscriptservice/V2");

        Selector().ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V2);
    }

    [Fact]
    public async Task ListSynthesisedForAnAgentThatPredatesTheCapabilitiesService_IsV1()
    {
        var synthesised = await new BackwardsCompatibleCapabilitiesClient(new NoCapabilitiesService()).GetCapabilitiesAsync(new CapabilitiesRequest());
        _cache.Store(MachineId, synthesised.Metadata, synthesised.AgentVersion, synthesised.SupportedServices);

        Selector().ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V1);
    }

    [Fact]
    public void MachineTheCacheHasNeverSeen_IsV1_EvenWhenAnotherMachineAdvertisesV2()
    {
        _cache.Store(7, new Dictionary<string, string>(), "1.0.0", new[] { ScriptServiceCapabilities.V2 });

        Selector().ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V1);
    }

    [Fact]
    public void NoCache_IsV1()
        => Selector(withCache: false).ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V1);

    [Fact]
    public void V2Advertised_ButNoV2DispatcherRegistered_IsV1()
    {
        Advertise(ScriptServiceCapabilities.V2);

        Selector(withV2: false).ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V1);
    }

    [Fact]
    public void Selecting_NeverTouchesAnAgent()
    {
        Advertise(ScriptServiceCapabilities.V2);

        var selector = Selector();
        selector.ForMachine(MachineId);
        selector.ForMachine(99);

        _v2Factory.Created.ShouldBe(0, "choosing a version must be a pure read of the advertised capabilities, never a call to the agent");
    }

    [Fact]
    public void ForVersion_ReturnsTheRecordedVersion_WhateverTheAgentAdvertisesNow()
    {
        Advertise("IScriptService/v1");

        Selector().ForVersion(ScriptProtocolVersion.V2).Version.ShouldBe(ScriptProtocolVersion.V2);
    }

    [Fact]
    public void ForVersion_NoDispatcherForThatVersion_Throws()
        => Should.Throw<UnsupportedScriptProtocolException>(() => Selector(withV2: false).ForVersion(ScriptProtocolVersion.V2));

    [Theory]
    [InlineData(false, ScriptProtocolVersion.V1, true)]
    [InlineData(false, ScriptProtocolVersion.V2, false)]
    [InlineData(true, ScriptProtocolVersion.V2, true)]
    public void Advertises_ReflectsTheCachedCapabilities_AndV1IsAlwaysTrue(bool advertiseV2, ScriptProtocolVersion version, bool expected)
    {
        if (advertiseV2) Advertise("IScriptService/v1", ScriptServiceCapabilities.V2);
        else Advertise("IScriptService/v1");

        Selector().Advertises(MachineId, version).ShouldBe(expected);
    }

    private sealed class CountingV2Factory : IScriptServiceV2ClientFactory
    {
        public int Created { get; private set; }

        public Squid.Message.Contracts.Tentacle.V2.IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint)
        {
            Created++;
            throw new InvalidOperationException("the selector must not open an agent client");
        }
    }

    private sealed class NoCapabilitiesService : IAsyncCapabilitiesService
    {
        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request)
            => throw new global::Halibut.Exceptions.ServiceNotFoundHalibutClientException("old agent");
    }
}
