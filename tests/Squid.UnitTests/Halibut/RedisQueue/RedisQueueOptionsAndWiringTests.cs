using System.Linq;
using Halibut;
using Halibut.Queue.Redis.Exceptions;
using Microsoft.Extensions.Configuration;
using Squid.Core.Halibut;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V1;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.UnitTests.Halibut.RedisQueue;

public class RedisQueueOptionsTests
{
    private static HalibutSetting Bind(params (string Key, string Value)[] pairs)
        => new(new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))).Build());

    [Fact]
    public void NoConfiguration_YieldsTheDocumentedDefaults()
    {
        var options = RedisQueueOptions.From(Bind());

        options.KeyPrefix.ShouldBe("squid");
        options.LivenessWindow.ShouldBe(TimeSpan.FromSeconds(15));
        options.FailureThreshold.ShouldBe(3);
        options.OpenDuration.ShouldBe(TimeSpan.FromSeconds(5));
        options.ProbeInterval.ShouldBe(TimeSpan.FromSeconds(3));
        options.ProbeTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        options.KeyRotation.ShouldBe(TimeSpan.FromMinutes(10));
    }

    // Renaming a key silently drops an operator's override; pin the section path and key names.
    [Fact]
    public void ConfigurationKeys_AreBound()
    {
        var options = RedisQueueOptions.From(Bind(
            ("Halibut:RedisQueue:KeyPrefix", "staging"),
            ("Halibut:RedisQueue:LivenessWindowSeconds", "40"),
            ("Halibut:RedisQueue:FailureThreshold", "7"),
            ("Halibut:RedisQueue:OpenDurationSeconds", "30"),
            ("Halibut:RedisQueue:ProbeIntervalSeconds", "9"),
            ("Halibut:RedisQueue:ProbeTimeoutSeconds", "4"),
            ("Halibut:RedisQueue:KeyRotationMinutes", "60")));

        options.ShouldBe(new RedisQueueOptions("staging", TimeSpan.FromSeconds(40), 7, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void ReplicaHeartbeatAndApplyResponseBudget_HaveDefaults_AreBound_AndLiveWindowIsThreeBeats()
    {
        var defaults = RedisQueueOptions.From(Bind());

        defaults.ReplicaHeartbeat.ShouldBe(TimeSpan.FromSeconds(5));
        defaults.ApplyResponseBudget.ShouldBe(TimeSpan.FromSeconds(10));
        defaults.ReplicaLiveWindow.ShouldBe(TimeSpan.FromSeconds(15));

        var bound = RedisQueueOptions.From(Bind(("Halibut:RedisQueue:ReplicaHeartbeatSeconds", "2"), ("Halibut:RedisQueue:ApplyResponseBudgetSeconds", "7")));

        bound.ReplicaHeartbeat.ShouldBe(TimeSpan.FromSeconds(2));
        bound.ApplyResponseBudget.ShouldBe(TimeSpan.FromSeconds(7));
        bound.ReplicaLiveWindow.ShouldBe(TimeSpan.FromSeconds(6));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void NonPositiveReplicaHeartbeatAndBudget_FallBackToTheDefaults(string raw)
    {
        var options = RedisQueueOptions.From(Bind(("Halibut:RedisQueue:ReplicaHeartbeatSeconds", raw), ("Halibut:RedisQueue:ApplyResponseBudgetSeconds", raw)));

        options.ShouldBe(RedisQueueOptions.From(Bind()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void NonPositiveNumbers_FallBackToTheDefaults(string raw)
    {
        var options = RedisQueueOptions.From(Bind(
            ("Halibut:RedisQueue:LivenessWindowSeconds", raw),
            ("Halibut:RedisQueue:FailureThreshold", raw),
            ("Halibut:RedisQueue:OpenDurationSeconds", raw),
            ("Halibut:RedisQueue:ProbeIntervalSeconds", raw),
            ("Halibut:RedisQueue:ProbeTimeoutSeconds", raw),
            ("Halibut:RedisQueue:KeyRotationMinutes", raw)));

        options.ShouldBe(RedisQueueOptions.From(Bind()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPrefix_FallsBackToTheDefault(string raw) => RedisQueueOptions.From(Bind(("Halibut:RedisQueue:KeyPrefix", raw))).KeyPrefix.ShouldBe("squid");

    [Fact]
    public void NullSetting_YieldsTheDefaults() => RedisQueueOptions.From(null).ShouldBe(RedisQueueOptions.From(Bind()));
}

public class RedisQueueFailureClassificationTests
{
    // Once a request is on Redis's queue its failures surface to the caller, and the pipeline pauses on a transient one and fails on any other.
    [Fact]
    public void EveryRedisQueueFailureHalibutRaises_IsTransient()
    {
        var inner = new InvalidOperationException("boom");

        TransientFailureClassifier.IsTransient(new CouldNotGetDataLossTokenInTimeHalibutClientException("x", inner)).ShouldBeTrue();
        TransientFailureClassifier.IsTransient(new ErrorOccuredWhenInsertingDataIntoRedisHalibutPendingRequestQueueHalibutClientException("x", inner)).ShouldBeTrue();
        TransientFailureClassifier.IsTransient(new ErrorWhilePreparingRequestForQueueHalibutClientException("x", inner)).ShouldBeTrue();
        TransientFailureClassifier.IsTransient(new RedisDataLossHalibutClientException("x")).ShouldBeTrue();
        TransientFailureClassifier.IsTransient(new RedisQueueShutdownClientException("x")).ShouldBeTrue();
        TargetCatchClassifier.IsTransientInfraFailure(new RedisDataLossHalibutClientException("x")).ShouldBeTrue("the decision the pipeline runner takes, not only the classifier behind it");
    }
}

public class HalibutQueueContractTests
{
    // A pod that dequeues a Redis request without ever having created a client for its contract cannot read the request; every contract an agent speaks must be registered at startup.
    // The expected set is derived, not listed: a contract a new agent version speaks, added to the Tentacle contracts, is missed by the registry and this fails.
    // A contract is a service interface of the Tentacle contract namespaces; the client-side async proxies (IAsync*) and the server-side async handlers (*Async) are not.
    [Fact]
    public void EveryAgentContract_IsRegisteredOnEveryPod()
    {
        var contracts = typeof(IScriptService).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace != null && t.Namespace.StartsWith("Squid.Message.Contracts.Tentacle", StringComparison.Ordinal))
            .Where(t => !t.Name.StartsWith("IAsync", StringComparison.Ordinal) && !t.Name.EndsWith("Async", StringComparison.Ordinal))
            .ToArray();

        contracts.Length.ShouldBeGreaterThanOrEqualTo(6, "the derivation found the contracts at all");
        HalibutQueueWiring.ContractTypes.ShouldBe(contracts, ignoreOrder: true);
    }

    // Every client the server creates for an agent is a contract the queue must be able to read on a pod that never created it.
    [Fact]
    public void EveryClientTheServerCreates_IsOfARegisteredContract()
    {
        var created = typeof(Squid.Core.Services.DeploymentExecution.Transport.IHalibutClientFactory).Assembly.GetTypes()
            .Where(t => typeof(Squid.Core.Services.DeploymentExecution.Transport.IHalibutClientFactory).IsAssignableFrom(t) && t.IsClass)
            .SelectMany(t => t.GetMethods())
            .Select(m => m.ReturnType)
            .Where(r => r.Namespace != null && r.Namespace.StartsWith("Squid.Message.Contracts.Tentacle", StringComparison.Ordinal) && r.Name.StartsWith("IAsync", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        created.ShouldNotBeEmpty("the factory's clients were found");

        foreach (var client in created)
        {
            var service = HalibutQueueWiring.ContractTypes.SingleOrDefault(c => client.Name == "IAsync" + c.Name[1..] || client.Name == "IAsyncClient" + c.Name[1..]);

            service.ShouldNotBeNull($"{client.Name} is created by the server, so its service contract must be registered");
        }
    }

    [Fact]
    public void TheContractRegistry_AcceptsTheMessageTypesOfEveryContract()
    {
        var registry = HalibutQueueWiring.BuildContractRegistry();

        registry.IsInAllowedTypes(typeof(StartScriptCommand)).ShouldBeTrue();
        registry.IsInAllowedTypes(typeof(ScriptStatusResponse)).ShouldBeTrue();
    }
}
