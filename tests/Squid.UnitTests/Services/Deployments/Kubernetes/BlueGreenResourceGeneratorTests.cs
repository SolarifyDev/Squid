using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;
using YamlDotNet.RepresentationModel;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class BlueGreenResourceGeneratorTests
{
    private const string ServicePortsJson = """[{"name":"http","port":"80","protocol":"TCP"}]""";

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
    public async Task Generate_BlueGreen_ScriptCarriesBothSlotDeployments()
    {
        var manifests = ExtractManifests(await GenerateBlueGreenScriptAsync());

        manifests.Select(m => (Kind(m), Name(m))).ShouldBe(new[] { ("Deployment", "test-deploy-blue"), ("Deployment", "test-deploy-green") }, ignoreOrder: true);
    }

    [Fact]
    public async Task Generate_BlueGreen_EmptyDeploymentLabels_SelectorStaysSlotOnly_PodsGetAppLabel()
    {
        // matchLabels is immutable: it must stay byte-compatible with what earlier releases
        // created ({slot}), otherwise re-applying an existing slot fails. The app label the
        // Service selector falls back to lives on the pod template only, which is mutable.
        var blue = ExtractManifests(await GenerateBlueGreenScriptAsync(("Squid.Action.KubernetesContainers.DeploymentLabels", "{}"))).Single(m => Name(m) == "test-deploy-blue");

        Map(blue, "spec", "selector", "matchLabels").ShouldBe(new Dictionary<string, string> { ["squid.io/deployment-slot"] = "blue" });
        Map(blue, "spec", "template", "metadata", "labels").ShouldBe(new Dictionary<string, string> { ["squid.io/deployment-slot"] = "blue", ["app"] = "test-deploy" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Generate_BlueGreen_ExplicitDeploymentLabels_PreservedInSelectorAndPods()
    {
        var green = ExtractManifests(await GenerateBlueGreenScriptAsync(("Squid.Action.KubernetesContainers.DeploymentLabels", """{"tier":"web"}"""))).Single(m => Name(m) == "test-deploy-green");
        var expected = new Dictionary<string, string> { ["tier"] = "web", ["squid.io/deployment-slot"] = "green" };

        Map(green, "spec", "selector", "matchLabels").ShouldBe(expected, ignoreOrder: true);
        Map(green, "spec", "template", "metadata", "labels").ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task Generate_BlueGreen_WithService_ServiceEmbeddedPerSlot_NotEmittedAsStaticFile()
    {
        // The script owns the Service so its selector is only ever written by the same apply
        // (same field manager): a separate service.yaml apply would conflict with — or, with
        // --force-conflicts, wipe — the slot selector under server-side apply.
        var result = await GenerateBlueGreenAsync(("Squid.Action.KubernetesContainers.ServiceName", "web-svc"), ("Squid.Action.KubernetesContainers.ServicePorts", ServicePortsJson));
        var services = ExtractManifests(Encoding.UTF8.GetString(result["bluegreen.sh"])).Where(m => Kind(m) == "Service").ToList();

        result.ShouldNotContainKey("service.yaml");
        services.Count.ShouldBe(2);
        services.ShouldAllBe(s => Name(s) == "web-svc");
        services.Select(s => Map(s, "spec", "selector")["squid.io/deployment-slot"]).ShouldBe(new[] { "blue", "green" }, ignoreOrder: true);
        services.ShouldAllBe(s => Map(s, "spec", "selector")["app"] == "test-deploy");
    }

    [Fact]
    public async Task Generate_BlueGreen_MalformedServicePorts_StillReportsConfigurationError()
    {
        await Should.ThrowAsync<ResourceGenerationException>(() => GenerateBlueGreenAsync(("Squid.Action.KubernetesContainers.ServiceName", "web-svc"), ("Squid.Action.KubernetesContainers.ServicePorts", "broken json")));
    }

    [Fact]
    public async Task Generate_BlueGreen_NoServiceConfigured_NeverTouchesAnyService()
    {
        var script = await GenerateBlueGreenScriptAsync();

        script.ShouldNotContain("kubectl get service");
        script.ShouldNotContain("kubectl patch");
        script.ShouldNotContain("kind: Service");
        script.ShouldNotContain("kubectl scale");
    }

    [Fact]
    public async Task Generate_BlueGreen_WithService_DerivesActiveSlotFromServiceSelector()
    {
        var script = await GenerateWithServiceScriptAsync();

        script.ShouldContain("kubectl get service \"web-svc\" -n \"test-ns\" --ignore-not-found -o name");
        script.ShouldContain("kubectl get service \"web-svc\" -n \"test-ns\" -o jsonpath='{.spec.selector.squid\\.io/deployment-slot}'");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("blue", "blue")]
    [InlineData("Green", "green")]
    [InlineData("purple", "")]
    public async Task Generate_BlueGreen_ActiveSlotPropertyIsFallbackSeed(string? propertyValue, string expectedSeed)
    {
        var extra = propertyValue == null
            ? Array.Empty<(string, string)>()
            : new[] { ("Squid.Action.KubernetesContainers.BlueGreenActiveSlot", propertyValue) };

        var script = await GenerateBlueGreenScriptAsync(extra);

        script.ShouldContain($"SQUID_BLUEGREEN_SEED_SLOT=\"{expectedSeed}\"");
    }

    [Fact]
    public async Task Generate_BlueGreen_PhasesOrdered_Detect_Pin_Apply_Rollout_Switch_ScaleDown()
    {
        var script = await GenerateWithServiceScriptAsync();

        var detect = script.IndexOf("kubectl get service", StringComparison.Ordinal);
        var pin = script.IndexOf("squid_bluegreen_service \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" |", StringComparison.Ordinal);
        var apply = script.IndexOf("squid_bluegreen_deployment \"$SQUID_BLUEGREEN_NEW_SLOT\" |", StringComparison.Ordinal);
        var rollout = script.IndexOf("kubectl rollout status", StringComparison.Ordinal);
        var switchTraffic = script.IndexOf("squid_bluegreen_service \"$SQUID_BLUEGREEN_NEW_SLOT\" |", StringComparison.Ordinal);
        var scale = script.IndexOf("kubectl scale deployment", StringComparison.Ordinal);

        detect.ShouldBeGreaterThan(-1);
        pin.ShouldBeGreaterThan(detect);
        apply.ShouldBeGreaterThan(pin);
        rollout.ShouldBeGreaterThan(apply);
        switchTraffic.ShouldBeGreaterThan(rollout);
        scale.ShouldBeGreaterThan(switchTraffic);
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

    [Fact]
    public async Task Generate_BlueGreen_ServerSideApplyFlagsHonoured_ForEveryApply()
    {
        var script = await GenerateWithServiceScriptAsync(("Squid.Action.Kubernetes.ServerSideApply.Enabled", "True"), ("Squid.Action.Kubernetes.ServerSideApply.ForceConflicts", "True"));
        var applies = Regex.Matches(script, "kubectl apply [^\n]*").Select(m => m.Value).ToList();

        applies.Count.ShouldBe(3);
        applies.ShouldAllBe(a => a == "kubectl apply -f \"-\" --server-side --field-manager=\"squid-deploy\" --force-conflicts");
    }

    [Fact]
    public async Task Generate_BlueGreen_StillGeneratesConfigMap()
    {
        var result = await GenerateBlueGreenAsync(("Squid.Action.KubernetesContainers.ConfigMapName", "cfg"), ("Squid.Action.KubernetesContainers.ConfigMapValues", """{"k":"v"}"""));

        result.ShouldContainKey("configmap.yaml");
        result.ShouldContainKey("bluegreen.sh");
    }

    [Fact]
    public async Task Generate_NonBlueGreen_DoesNotGenerateBlueGreenFiles_AndKeepsStaticService()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");
        Add(action, "Squid.Action.KubernetesContainers.ServiceName", "web-svc");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts", ServicePortsJson);

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("deployment.yaml");
        result.ShouldContainKey("service.yaml");
        result.ShouldNotContainKey("bluegreen.sh");

        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("name: \"test-deploy\"");
        yaml.ShouldNotContain("test-deploy-green");
        yaml.ShouldNotContain("test-deploy-blue");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static List<YamlMappingNode> ExtractManifests(string script)
    {
        var bodies = Regex.Matches(script, "<<'SQUID_BLUEGREEN_MANIFEST'\n(.*?)\nSQUID_BLUEGREEN_MANIFEST\n", RegexOptions.Singleline);

        return bodies.Select(m => ParseYaml(m.Groups[1].Value)).ToList();
    }

    private static YamlMappingNode ParseYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Kind(YamlMappingNode manifest) => ((YamlScalarNode)manifest["kind"]).Value!;

    private static string Name(YamlMappingNode manifest) => ((YamlScalarNode)((YamlMappingNode)manifest["metadata"])["name"]).Value!;

    private static Dictionary<string, string> Map(YamlMappingNode manifest, params string[] path)
    {
        var node = path.Aggregate(manifest, (current, key) => (YamlMappingNode)current[key]);

        return node.Children.ToDictionary(kvp => ((YamlScalarNode)kvp.Key).Value!, kvp => ((YamlScalarNode)kvp.Value).Value!);
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

    private Task<string> GenerateWithServiceScriptAsync(params (string Name, string Value)[] extra)
        => GenerateBlueGreenScriptAsync(extra.Concat(new[] { ("Squid.Action.KubernetesContainers.ServiceName", "web-svc"), ("Squid.Action.KubernetesContainers.ServicePorts", ServicePortsJson) }).ToArray());

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
