using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Api;

public class KubernetesDeployServiceE2ETests : KubernetesApiE2ETestBase
{
    private readonly KubernetesApiContextScriptBuilder _contextBuilder = new();
    private readonly KubernetesDeployServiceActionHandler _handler = new();

    public KubernetesDeployServiceE2ETests(KindClusterFixture cluster) : base(cluster)
    {
    }

    [Fact]
    public async Task DeployService_ClusterIP_SinglePort_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildServiceAction("e2e-svc-basic", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\",\"protocol\":\"TCP\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Service failed: {scriptResult.StdErr}");

            var svcType = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-basic -o jsonpath='{{.spec.type}}'");
            svcType.Trim('\'').ShouldBe("ClusterIP");

            var svcPort = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-basic -o jsonpath='{{.spec.ports[0].port}}'");
            svcPort.Trim('\'').ShouldBe("80");

            var targetPort = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-basic -o jsonpath='{{.spec.ports[0].targetPort}}'");
            targetPort.Trim('\'').ShouldBe("8080");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_MultiplePorts_AllPortsPresent()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var portsJson = "[" +
                "{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\",\"protocol\":\"TCP\"}," +
                "{\"name\":\"https\",\"port\":443,\"targetPort\":\"8443\",\"protocol\":\"TCP\"}," +
                "{\"name\":\"metrics\",\"port\":9090,\"targetPort\":\"9090\",\"protocol\":\"TCP\"}" +
                "]";

            var action = BuildServiceAction("e2e-svc-multi", testNs, portsJson: portsJson);

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Service failed: {scriptResult.StdErr}");

            var svcYaml = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-multi -o yaml");
            svcYaml.ShouldContain("port: 80");
            svcYaml.ShouldContain("port: 443");
            svcYaml.ShouldContain("port: 9090");

            var portCount = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-multi -o jsonpath='{{.spec.ports[*].port}}'");
            var ports = portCount.Trim('\'').Split(' ');
            ports.Length.ShouldBe(3);
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_NodePort_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildServiceAction("e2e-svc-np", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\",\"nodePort\":30080,\"protocol\":\"TCP\"}]",
                serviceType: "NodePort");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy NodePort Service failed: {scriptResult.StdErr}");

            var svcType = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-np -o jsonpath='{{.spec.type}}'");
            svcType.Trim('\'').ShouldBe("NodePort");

            var nodePort = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-np -o jsonpath='{{.spec.ports[0].nodePort}}'");
            nodePort.Trim('\'').ShouldBe("30080");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_WithAnnotations_AnnotationsApplied()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildServiceAction("e2e-svc-ann", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\"}]",
                annotationsJson: "{\"service.beta.kubernetes.io/load-balancer-type\":\"nlb\",\"custom.io/team\":\"platform\"}");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Service failed: {scriptResult.StdErr}");

            var annotations = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-ann -o jsonpath='{{.metadata.annotations}}'");
            annotations.ShouldContain("load-balancer-type");
            annotations.ShouldContain("nlb");
            annotations.ShouldContain("custom.io/team");
            annotations.ShouldContain("platform");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_CustomSelector_SelectorApplied()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildServiceAction("e2e-svc-sel", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\"}]",
                deploymentLabels: "{\"app\":\"custom-app\",\"tier\":\"frontend\"}");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Service failed: {scriptResult.StdErr}");

            var selector = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-sel -o jsonpath='{{.spec.selector}}'");
            selector.ShouldContain("custom-app");
            selector.ShouldContain("frontend");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_DefaultSelector_UsesServiceName()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildServiceAction("e2e-svc-defsel", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy Service failed: {scriptResult.StdErr}");

            var selectorApp = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-defsel -o jsonpath='{{.spec.selector.app}}'");
            selectorApp.Trim('\'').ShouldBe("e2e-svc-defsel");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_Update_OverwritesExisting()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var firstAction = BuildServiceAction("e2e-svc-upd", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":80,\"targetPort\":\"8080\"}]");
            var firstResult = await PrepareAndAssertNotNull(firstAction);
            var firstScript = await ApplyToClusterAsync(firstResult, clusterUrl, token, testNs);
            firstScript.ExitCode.ShouldBe(0, $"First apply failed: {firstScript.StdErr}");

            var secondAction = BuildServiceAction("e2e-svc-upd", testNs,
                portsJson: "[{\"name\":\"http\",\"port\":8080,\"targetPort\":\"9090\"}]");
            var secondResult = await PrepareAndAssertNotNull(secondAction);
            var secondScript = await ApplyToClusterAsync(secondResult, clusterUrl, token, testNs);
            secondScript.ExitCode.ShouldBe(0, $"Second apply failed: {secondScript.StdErr}");

            var svcPort = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-upd -o jsonpath='{{.spec.ports[0].port}}'");
            svcPort.Trim('\'').ShouldBe("8080");

            var targetPort = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-upd -o jsonpath='{{.spec.ports[0].targetPort}}'");
            targetPort.Trim('\'').ShouldBe("9090");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task DeployService_NoName_ReturnsNull()
    {
        var action = BuildServiceAction("", "default",
            portsJson: "[{\"name\":\"http\",\"port\":80}]");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeployService_NoPorts_ReturnsNull()
    {
        var action = BuildServiceAction("some-svc", "default", portsJson: "[]");

        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeployService_UdpProtocol_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();
        var token = await GetServiceAccountTokenAsync();
        var testNs = $"squid-svc-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await Cluster.KubectlAsync($"create namespace {testNs}");

            var action = BuildServiceAction("e2e-svc-udp", testNs,
                portsJson: "[{\"name\":\"dns\",\"port\":53,\"targetPort\":\"53\",\"protocol\":\"UDP\"}]");

            var result = await PrepareAndAssertNotNull(action);
            var scriptResult = await ApplyToClusterAsync(result, clusterUrl, token, testNs);

            scriptResult.ExitCode.ShouldBe(0, $"Deploy UDP Service failed: {scriptResult.StdErr}");

            var protocol = await Cluster.KubectlAsync($"-n {testNs} get service e2e-svc-udp -o jsonpath='{{.spec.ports[0].protocol}}'");
            protocol.Trim('\'').ShouldBe("UDP");
        }
        finally
        {
            await Cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DeploymentActionDto BuildServiceAction(string serviceName, string namespaceName, string portsJson, string serviceType = null, string annotationsJson = null, string deploymentLabels = null, string deploymentName = null)
    {
        var properties = new List<DeploymentActionPropertyDto>
        {
            new() { PropertyName = "Squid.Action.KubernetesContainers.ServiceName", PropertyValue = serviceName },
            new() { PropertyName = "Squid.Action.Kubernetes.Namespace", PropertyValue = namespaceName }
        };

        if (portsJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.ServicePorts", PropertyValue = portsJson });

        if (serviceType != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.ServiceType", PropertyValue = serviceType });

        if (annotationsJson != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.ServiceAnnotations", PropertyValue = annotationsJson });

        if (deploymentLabels != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.DeploymentLabels", PropertyValue = deploymentLabels });

        if (deploymentName != null)
            properties.Add(new() { PropertyName = "Squid.Action.KubernetesContainers.DeploymentName", PropertyValue = deploymentName });

        return new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployService",
            Properties = properties
        };
    }

    private async Task<ActionExecutionResult> PrepareAndAssertNotNull(DeploymentActionDto action)
    {
        var ctx = new ActionExecutionContext { Action = action };
        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Files.ShouldContainKey("service.yaml");

        return result;
    }

    private async Task<ScriptResult> ApplyToClusterAsync(ActionExecutionResult result, string clusterUrl, string token, string ns)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var file in result.Files)
                await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);

            var modifiedScript = $"cd \"{tempDir}\"\n{result.ScriptBody}";
            var scriptContext = MakeScriptContext(clusterUrl, token, ns);
            var fullScript = _contextBuilder.WrapWithContext(modifiedScript, scriptContext);

            return await ExecuteBashScriptAsync(fullScript);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static ScriptContext MakeScriptContext(string clusterUrl, string token, string ns)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = ns,
                SkipTlsVerification = "True"
            })
        };
        endpoint.SetAccountData(AccountType.Token, DeploymentAccountCredentialsConverter.Serialize(
            new TokenCredentials { Token = token }));

        return new ScriptContext
        {
            Endpoint = endpoint,
            Syntax = ScriptSyntax.Bash
        };
    }
}
