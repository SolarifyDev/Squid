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
    [Fact]
    public void EveryAgentContract_IsRegisteredOnEveryPod()
    {
        HalibutQueueWiring.ContractTypes.ShouldBe([typeof(IScriptService), typeof(ICapabilitiesService), typeof(IFileTransferService), typeof(IScriptServiceV1), typeof(ICapabilitiesServiceV1), typeof(IScriptServiceV2)], ignoreOrder: true);
    }

    [Fact]
    public void TheContractRegistry_AcceptsTheMessageTypesOfEveryContract()
    {
        var registry = HalibutQueueWiring.BuildContractRegistry();

        registry.IsInAllowedTypes(typeof(StartScriptCommand)).ShouldBeTrue();
        registry.IsInAllowedTypes(typeof(ScriptStatusResponse)).ShouldBeTrue();
    }
}
