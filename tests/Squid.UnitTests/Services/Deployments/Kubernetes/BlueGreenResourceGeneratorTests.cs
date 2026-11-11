using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class BlueGreenResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    [Theory]
    [InlineData(null, "green")]
    [InlineData("", "green")]
    [InlineData("blue", "green")]
    [InlineData("green", "blue")]
    public void ResolveNewSlot_AlternatesCorrectly(string? currentSlot, string expectedSlot)
    {
        BlueGreenResourceGenerator.ResolveNewSlot(currentSlot).ShouldBe(expectedSlot);
    }

    [Fact]
    public void ScriptFileName_ConstantPinned()
    {
        // The handler routes this file into KubernetesApplyIntent.PostApplyScripts by its
        // .sh extension; renaming it to anything that is not a shell script silently drops
        // the traffic switch again.
        BlueGreenResourceGenerator.ScriptFileName.ShouldBe("bluegreen.sh");
    }

    [Fact]
    public async Task Generate_BlueGreen_EmitsSingleRuntimeScript_AndNoStaticDeploymentManifest()
    {
        var result = await GenerateBlueGreenAsync();

        result.ShouldContainKey("bluegreen.sh");
        result.ShouldNotContainKey("deployment.yaml");
        result.ShouldNotContainKey("bluegreen-switch.sh");
        result.ShouldNotContainKey("bluegreen-scaledown.sh");
    }

    [Fact]
    public async Task Generate_BlueGreen_ScriptCarriesBothSlotManifests()
    {
        var script = await GenerateBlueGreenScriptAsync();

        script.ShouldContain("name: \"test-deploy-blue\"");
        script.ShouldContain("name: \"test-deploy-green\"");
        script.ShouldContain("\"squid.io/deployment-slot\": \"blue\"");
        script.ShouldContain("\"squid.io/deployment-slot\": \"green\"");
    }

    [Fact]
    public async Task Generate_BlueGreen_EmptyDeploymentLabels_SeedsAppLabelSoServiceSelectorMatches()
    {
        // Service selector falls back to app=<deployment name> when DeploymentLabels is empty.
        // Pods must carry that label too, otherwise neither slot is ever routable.
        var script = await GenerateBlueGreenScriptAsync(("Squid.Action.KubernetesContainers.DeploymentLabels", "{}"));

        script.ShouldContain("\"app\": \"test-deploy\"");
    }

    [Fact]
    public async Task Generate_BlueGreen_ExplicitDeploymentLabels_Preserved()
    {
        var script = await GenerateBlueGreenScriptAsync(("Squid.Action.KubernetesContainers.DeploymentLabels", """{"tier":"web"}"""));

        script.ShouldContain("\"tier\": \"web\"");
        script.ShouldNotContain("\"app\": \"test-deploy\"");
    }

    [Fact]
    public async Task Generate_BlueGreen_DerivesActiveSlotFromServiceSelector()
    {
        var script = await GenerateBlueGreenScriptAsync(("Squid.Action.KubernetesContainers.ServiceName", "web-svc"));

        script.ShouldContain("kubectl get service \"web-svc\" -n \"test-ns\" -o jsonpath='{.spec.selector.squid\\.io/deployment-slot}'");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("blue", "blue")]
    [InlineData("Green", "green")]
    [InlineData("purple", "")]
    public async Task Generate_BlueGreen_ActiveSlotPropertyIsFallbackSeed(string? propertyValue, string expectedFallback)
    {
        var extra = propertyValue == null
            ? Array.Empty<(string, string)>()
            : new[] { ("Squid.Action.KubernetesContainers.BlueGreenActiveSlot", propertyValue) };

        var script = await GenerateBlueGreenScriptAsync(extra);

        script.ShouldContain($"SQUID_BLUEGREEN_ACTIVE_SLOT=\"{expectedFallback}\"");
    }

    [Fact]
    public async Task Generate_BlueGreen_PhasesOrdered_Detect_Apply_Rollout_Switch_ScaleDown()
    {
        var script = await GenerateBlueGreenScriptAsync(("Squid.Action.KubernetesContainers.ServiceName", "web-svc"));

        var detect = script.IndexOf("kubectl get service", StringComparison.Ordinal);
        var apply = script.IndexOf("kubectl apply -f \"-\"", StringComparison.Ordinal);
        var rollout = script.IndexOf("kubectl rollout status", StringComparison.Ordinal);
        var patch = script.IndexOf("kubectl patch service", StringComparison.Ordinal);
        var scale = script.IndexOf("kubectl scale deployment", StringComparison.Ordinal);

        detect.ShouldBeGreaterThan(-1);
        apply.ShouldBeGreaterThan(detect);
        rollout.ShouldBeGreaterThan(apply);
        patch.ShouldBeGreaterThan(rollout);
        scale.ShouldBeGreaterThan(patch);
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData("120", 120)]
    [InlineData("not-a-number", 300)]
    [InlineData("0", 300)]
    public async Task Generate_BlueGreen_RolloutTimeoutFromStatusCheckTimeout(string? timeout, int expectedSeconds)
    {
        var extra = timeout == null
            ? Array.Empty<(string, string)>()
            : new[] { ("Squid.Action.KubernetesContainers.ObjectStatusCheckTimeout", timeout) };

        var script = await GenerateBlueGreenScriptAsync(extra);

        script.ShouldContain($"--timeout={expectedSeconds}s");
    }

    [Theory]
    [InlineData("web-svc", "web-svc")]
    [InlineData(null, "test-deploy")]
    public async Task Generate_BlueGreen_SwitchTargetsServiceName_FallingBackToDeploymentName(string? serviceName, string expectedService)
    {
        var extra = serviceName == null
            ? Array.Empty<(string, string)>()
            : new[] { ("Squid.Action.KubernetesContainers.ServiceName", serviceName) };

        var script = await GenerateBlueGreenScriptAsync(extra);

        script.ShouldContain($"kubectl patch service \"{expectedService}\"");
    }

    [Fact]
    public async Task Generate_BlueGreen_ServerSideApplyFlagsHonoured()
    {
        var script = await GenerateBlueGreenScriptAsync(
            ("Squid.Action.Kubernetes.ServerSideApply.Enabled", "True"),
            ("Squid.Action.Kubernetes.ServerSideApply.ForceConflicts", "True"));

        script.ShouldContain("kubectl apply -f \"-\" --server-side --field-manager=\"squid-deploy\" --force-conflicts");
    }

    [Fact]
    public async Task Generate_BlueGreen_StillGeneratesServiceAndConfigMap()
    {
        var result = await GenerateBlueGreenAsync(
            ("Squid.Action.KubernetesContainers.ServiceName", "test-svc"),
            ("Squid.Action.KubernetesContainers.ServicePorts", """[{"name":"http","port":"80","protocol":"TCP"}]"""));

        result.ShouldContainKey("service.yaml");
        result.ShouldContainKey("bluegreen.sh");
    }

    [Fact]
    public async Task Generate_NonBlueGreen_DoesNotGenerateBlueGreenFiles()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("deployment.yaml");
        result.ShouldNotContainKey("bluegreen.sh");

        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("name: \"test-deploy\"");
        yaml.ShouldNotContain("test-deploy-green");
        yaml.ShouldNotContain("test-deploy-blue");
    }

    [Fact]
    public void GenerateSwitchScript_PatchesSelectorToRuntimeNewSlot()
    {
        var script = BlueGreenResourceGenerator.GenerateSwitchScript("my-svc", "production");

        script.ShouldContain("kubectl patch service \"my-svc\" -n \"production\"");
        script.ShouldContain("/spec/selector/squid.io~1deployment-slot");
        script.ShouldContain("\"value\": \"'\"$SQUID_BLUEGREEN_NEW_SLOT\"'\"");
    }

    [Fact]
    public void GenerateScaleDownScript_TargetsRuntimeOldSlot()
    {
        var script = BlueGreenResourceGenerator.GenerateScaleDownScript("my-deploy", "production");

        script.ShouldContain("kubectl scale deployment \"my-deploy-$SQUID_BLUEGREEN_OLD_SLOT\" -n \"production\" --replicas=0");
    }

    private async Task<Dictionary<string, byte[]>> GenerateBlueGreenAsync(params (string Name, string Value)[] extra)
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");

        foreach (var (name, value) in extra)
            Add(action, name, value);

        return await _compositor.GenerateAsync(step, action, CancellationToken.None);
    }

    private async Task<string> GenerateBlueGreenScriptAsync(params (string Name, string Value)[] extra)
    {
        var result = await GenerateBlueGreenAsync(extra);

        return Encoding.UTF8.GetString(result["bluegreen.sh"]);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.DeploymentName", "test-deploy");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "test-ns");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Ports":[{"key":"http","value":"80","option":"TCP"}]}]""");

        return (step, action);
    }

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = name,
            PropertyValue = value
        });
    }
}
