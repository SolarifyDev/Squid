using System.IO;
using System.Linq;
using Halibut;
using Halibut.Diagnostics;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines;
using Xunit;
using Environment = System.Environment;

namespace Squid.UnitTests.Halibut;

/// <summary>
/// Halibut copies PollingRequestQueueTimeout / RetryListeningSleepInterval / RetryCountLimit /
/// ConnectionErrorRetryTimeout / TcpClientConnectTimeout onto each <see cref="ServiceEndPoint"/> at
/// construction and reads them from the request destination at dispatch, so env overrides applied
/// only to the runtime never reach a polling dispatch. These tests pin that every endpoint the server
/// builds carries the same SSOT limits as the runtime.
/// </summary>
[Collection(HalibutEnvVarCollection.Name)]
public sealed class HalibutEndpointTimeoutsTests
{
    private const string PollingJson = "{\"CommunicationStyle\":\"TentaclePolling\",\"SubscriptionId\":\"sub-1\",\"Thumbprint\":\"AABBCC\"}";
    private const string ListeningJson = "{\"CommunicationStyle\":\"TentacleListening\",\"Uri\":\"https://agent.example:10933/\",\"Thumbprint\":\"AABBCC\"}";

    [Fact]
    public void NoEnvVars_EndpointLimitsEqualRecommendedValues_ForPollingAndListening()
    {
        WithEnv(null, null, () =>
        {
            var defaults = HalibutTimeoutsAndLimits.RecommendedValues();

            AssertEndpointMatches(EndpointJsonHelper.ParseHalibutEndpoint(PollingJson), defaults);
            AssertEndpointMatches(EndpointJsonHelper.ParseTentacleListeningEndpoint(ListeningJson), defaults);
        });
    }

    [Fact]
    public void NoEnvVars_RuntimeLimitsEqualRecommendedValues_FieldByField()
    {
        WithEnv(null, null, () =>
        {
            var defaults = HalibutTimeoutsAndLimits.RecommendedValues();
            var built = HalibutModule.BuildTimeoutsAndLimits();

            foreach (var property in typeof(HalibutTimeoutsAndLimits).GetProperties())
            {
                var expected = property.GetValue(defaults);
                var actual = property.GetValue(built);

                if (expected is SendReceiveTimeout e)
                {
                    var a = (SendReceiveTimeout)actual;
                    a.SendTimeout.ShouldBe(e.SendTimeout, property.Name);
                    a.ReceiveTimeout.ShouldBe(e.ReceiveTimeout, property.Name);
                    continue;
                }

                actual.ShouldBe(expected, property.Name);
            }
        });
    }

    [Fact]
    public void PollingQueueOverride_ReachesRuntimeAndBothEndpointKinds()
    {
        WithEnv(null, "3", () =>
        {
            HalibutModule.BuildTimeoutsAndLimits().PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(3));

            EndpointJsonHelper.ParseHalibutEndpoint(PollingJson).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(3));
            EndpointJsonHelper.ParseTentacleListeningEndpoint(ListeningJson).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(3));
        });
    }

    [Fact]
    public void TcpReceiveOverride_ReachesRuntimeLimits_AndLeavesEndpointFieldsAtDefaults()
    {
        WithEnv("77", null, () =>
        {
            HalibutModule.BuildTimeoutsAndLimits().TcpClientReceiveResponseTimeout.ShouldBe(TimeSpan.FromSeconds(77));

            // The receive-response timeout is runtime-level in Halibut (MessageExchangeStream); the endpoint
            // carries only the five copied fields, which must stay at their defaults here.
            AssertEndpointMatches(EndpointJsonHelper.ParseHalibutEndpoint(PollingJson), HalibutTimeoutsAndLimits.RecommendedValues());
        });
    }

    [Fact]
    public void EveryProductionEndpointParser_CarriesTheOverride()
    {
        WithEnv(null, "9", () =>
        {
            var polling = new Machine { Name = "m", Endpoint = PollingJson };
            var listening = new Machine { Name = "m", Endpoint = ListeningJson };

            HalibutMachineExecutionStrategy.ParseMachineEndpoint(polling).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(9));
            HalibutMachineExecutionStrategy.ParseMachineEndpoint(listening).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(9));
            TentacleHealthCheckStrategy.ParseEndpoint(polling).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(9));
            TentacleHealthCheckStrategy.ParseEndpoint(listening).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(9));
            KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(polling).PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(9));
        });
    }

    [Fact]
    public void ServerSource_ConstructsServiceEndPointsOnlyInEndpointJsonHelper_UsingTheSsot()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var allowed = Path.Combine("Squid.Core", "Services", "Machines", "EndpointJsonHelper.cs");
        var sep = Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}Squid.Tentacle{sep}") && !f.Contains($"{sep}obj{sep}"))
            .Where(f => File.ReadAllText(f).Contains("new ServiceEndPoint(") && !f.EndsWith(allowed))
            .ToList();

        offenders.ShouldBeEmpty("Server-side ServiceEndPoint must be built via EndpointJsonHelper so operator env overrides apply (drift: new construction site found). Add it to the SSOT instead.");

        var helper = File.ReadAllText(Path.Combine(srcRoot, allowed));

        helper.ShouldNotContain("RecommendedValues()", customMessage: "EndpointJsonHelper must use HalibutModule.BuildTimeoutsAndLimits(), not RecommendedValues().");
        helper.Split("new ServiceEndPoint(").Length.ShouldBe(3, "expected exactly the polling + listening construction sites");
        helper.Split("HalibutModule.BuildTimeoutsAndLimits()").Length.ShouldBe(3);
    }

    private static void AssertEndpointMatches(ServiceEndPoint endpoint, HalibutTimeoutsAndLimits expected)
    {
        endpoint.PollingRequestQueueTimeout.ShouldBe(expected.PollingRequestQueueTimeout);
        endpoint.RetryListeningSleepInterval.ShouldBe(expected.RetryListeningSleepInterval);
        endpoint.RetryCountLimit.ShouldBe(expected.RetryCountLimit);
        endpoint.ConnectionErrorRetryTimeout.ShouldBe(expected.ConnectionErrorRetryTimeout);
        endpoint.TcpClientConnectTimeout.ShouldBe(expected.TcpClientConnectTimeout);
    }

    private static void WithEnv(string tcpReceive, string pollingQueue, Action body)
    {
        Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, tcpReceive);
        Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, pollingQueue);

        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, null);
            Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, null);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Squid.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Squid.sln not found above test base directory");
    }
}
