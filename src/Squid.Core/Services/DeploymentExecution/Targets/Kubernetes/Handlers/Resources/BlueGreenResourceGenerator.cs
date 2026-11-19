using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

/// <summary>
/// Generates the Blue/Green runtime script (<see cref="ScriptFileName"/>). Both slot Deployments
/// and — when a Service is configured — both slot variants of the Service are embedded, and the
/// script runs on the target after the other manifests are applied:
/// 1. Derives the live slot from the Service selector label <c>squid.io/deployment-slot</c>,
///    falling back to the <c>BlueGreenActiveSlot</c> property, then to the only slot Deployment
///    that exists (the state earlier releases left behind). The cluster is the single source of
///    truth, so every target / namespace alternates independently.
/// 2. Pins the Service to the live slot, so the new slot's pods take no traffic during rollout
/// 3. Applies the Deployment for the other slot
/// 4. Waits for its rollout to succeed
/// 5. Applies the Service for the new slot (the traffic switch)
/// 6. Scales the old slot's Deployment to zero
///
/// The Service is applied only by this script, with the same apply command as every other
/// manifest, so its selector always has a single field manager — under server-side apply a
/// separate <c>kubectl patch</c> would make the next deploy conflict on <c>.spec.selector</c>.
/// Without a configured Service there is no traffic to switch: the new slot is rolled out and
/// nothing else is touched.
/// </summary>
internal sealed class BlueGreenResourceGenerator
{
    internal const string ScriptFileName = "bluegreen.sh";

    private const string Blue = "blue";
    private const string Green = "green";
    private const string ManifestDelimiter = "SQUID_BLUEGREEN_MANIFEST";
    private const string DeploymentFunction = "squid_bluegreen_deployment";
    private const string ServiceFunction = "squid_bluegreen_service";
    private const int DefaultRolloutTimeoutSeconds = 300;

    private readonly DeploymentResourceGenerator _deploymentGenerator = new();
    private readonly ServiceResourceGenerator _serviceGenerator = new();

    private sealed record ScriptPlan(string BaseName, string NamespaceFlag, string SeedActiveSlot, string ApplyCommand, int RolloutTimeoutSeconds, string ServiceName);

    private sealed record SlotManifests(string BlueManifest, string GreenManifest);

    internal static string ResolveNewSlot(string? currentSlot)
    {
        return string.Equals(currentSlot, Green, StringComparison.OrdinalIgnoreCase)
            ? Blue
            : Green;
    }

    internal static string BuildVersionedDeploymentName(string baseName, string slot)
        => $"{baseName}-{slot}";

    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var style = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentStyle);
        return string.Equals(style, KubernetesDeploymentStrategyValues.BlueGreen, StringComparison.OrdinalIgnoreCase)
               && _deploymentGenerator.CanGenerate(properties);
    }

    public Dictionary<string, string> GenerateAll(Dictionary<string, string> properties)
    {
        var plan = BuildPlan(properties);
        var deployments = new SlotManifests(GenerateSlotDeployment(properties, Blue), GenerateSlotDeployment(properties, Green));
        var services = plan.ServiceName == null ? null : new SlotManifests(GenerateSlotService(properties, Blue), GenerateSlotService(properties, Green));

        return new Dictionary<string, string> { [ScriptFileName] = GenerateScript(plan, deployments, services) };
    }

    // ── Plan ────────────────────────────────────────────────────────────────

    private ScriptPlan BuildPlan(Dictionary<string, string> properties)
    {
        var serviceName = _serviceGenerator.CanGenerate(properties) ? KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceName) : null;
        var namespaceFlag = BuildNamespaceFlag(KubernetesPropertyParser.GetNamespace(properties));

        return new ScriptPlan(GetBaseName(properties), namespaceFlag, ReadSeedActiveSlot(properties), BuildApplyCommand(properties), ReadRolloutTimeoutSeconds(properties), serviceName);
    }

    private static string GetBaseName(Dictionary<string, string> properties)
        => KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);

    private static string ReadSeedActiveSlot(Dictionary<string, string> properties)
    {
        var slot = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.BlueGreenActiveSlot)?.Trim().ToLowerInvariant();

        return slot is Blue or Green ? slot : string.Empty;
    }

    private static int ReadRolloutTimeoutSeconds(Dictionary<string, string> properties)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ObjectStatusCheckTimeout);

        return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : DefaultRolloutTimeoutSeconds;
    }

    private static string BuildApplyCommand(Dictionary<string, string> properties)
    {
        var serverSide = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServerSideApplyEnabled) == KubernetesBooleanValues.True;
        var fieldManager = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServerSideApplyFieldManager);
        var forceConflicts = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True;

        return KubernetesApplyCommandBuilder.Build("-", serverSide, fieldManager, forceConflicts);
    }

    private static string BuildNamespaceFlag(string namespaceName)
        => string.IsNullOrWhiteSpace(namespaceName) ? "" : $" -n \"{namespaceName}\"";

    // ── Manifests ───────────────────────────────────────────────────────────

    /// <summary>
    /// <c>matchLabels</c> is immutable, so it stays exactly what earlier releases created
    /// (configured labels + slot). With no configured labels the Service selects
    /// <c>app: {baseName}</c>; that label is added to the pod template only.
    /// </summary>
    private string GenerateSlotDeployment(Dictionary<string, string> properties, string slot)
    {
        var baseName = GetBaseName(properties);
        var configuredLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);

        var slotProperties = WithSlotLabels(properties, configuredLabels, slot);
        slotProperties[KubernetesProperties.DeploymentName] = BuildVersionedDeploymentName(baseName, slot);
        slotProperties[KubernetesProperties.DeploymentStyle] = KubernetesDeploymentStrategyValues.RollingUpdate;

        var extraPodLabels = configuredLabels.Count > 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { [KubernetesLabelKeys.App] = baseName };

        return _deploymentGenerator.Generate(slotProperties, extraPodLabels);
    }

    /// <summary>The Service selector: configured labels (or the <c>app: {baseName}</c> fallback) plus the slot.</summary>
    private string GenerateSlotService(Dictionary<string, string> properties, string slot)
    {
        var selectorLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);

        if (selectorLabels.Count == 0)
            selectorLabels[KubernetesLabelKeys.App] = GetBaseName(properties);

        return _serviceGenerator.Generate(WithSlotLabels(properties, selectorLabels, slot));
    }

    private static Dictionary<string, string> WithSlotLabels(Dictionary<string, string> properties, Dictionary<string, string> labels, string slot)
    {
        var slotLabels = new Dictionary<string, string>(labels) { [KubernetesLabelKeys.DeploymentSlot] = slot };

        return new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase) { [KubernetesProperties.DeploymentLabels] = JsonSerializer.Serialize(slotLabels) };
    }

    // ── Script ──────────────────────────────────────────────────────────────

    private static string GenerateScript(ScriptPlan plan, SlotManifests deployments, SlotManifests services)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -euo pipefail");

        AppendSection(sb, GenerateManifestFunction(DeploymentFunction, deployments));
        if (services != null) AppendSection(sb, GenerateManifestFunction(ServiceFunction, services));
        AppendSection(sb, GenerateSlotDetectionScript(plan));
        if (services != null) AppendSection(sb, GeneratePinScript(plan));
        AppendSection(sb, GenerateSlotApplyScript(plan));
        AppendSection(sb, GenerateRolloutWaitScript(plan));

        if (services == null)
        {
            AppendSection(sb, GenerateNoServiceNotice());
            return sb.ToString();
        }

        AppendSection(sb, GenerateSwitchScript(plan));
        AppendSection(sb, GenerateScaleDownScript(plan));

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string section)
    {
        sb.AppendLine();
        sb.Append(section);
    }

    private static string GenerateManifestFunction(string functionName, SlotManifests manifests)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{functionName}() {{");
        sb.AppendLine("  case \"$1\" in");
        AppendManifestCase(sb, Blue, manifests.BlueManifest);
        AppendManifestCase(sb, Green, manifests.GreenManifest);
        sb.AppendLine("  esac");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendManifestCase(StringBuilder sb, string slot, string manifest)
    {
        sb.AppendLine($"    {slot})");
        sb.AppendLine($"cat <<'{ManifestDelimiter}'");
        sb.AppendLine(manifest.TrimEnd());
        sb.AppendLine(ManifestDelimiter);
        sb.AppendLine("      ;;");
    }

    private static string GenerateSlotDetectionScript(ScriptPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: derive the live slot (cluster is the source of truth).");
        sb.AppendLine($"SQUID_BLUEGREEN_SEED_SLOT=\"{plan.SeedActiveSlot}\"");
        sb.AppendLine("SQUID_BLUEGREEN_SERVICE_EXISTS=false");
        sb.AppendLine("SQUID_BLUEGREEN_ACTIVE_SLOT=\"\"");
        if (plan.ServiceName != null) sb.Append(GenerateServiceSelectorProbe(plan));
        sb.AppendLine($"case \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" in {Blue}|{Green}) ;; *) SQUID_BLUEGREEN_ACTIVE_SLOT=\"$SQUID_BLUEGREEN_SEED_SLOT\" ;; esac");
        sb.Append(GenerateExistingSlotInference(plan));
        sb.AppendLine($"if [ \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" = \"{Green}\" ]; then");
        sb.AppendLine($"  SQUID_BLUEGREEN_NEW_SLOT=\"{Blue}\"; SQUID_BLUEGREEN_OLD_SLOT=\"{Green}\"");
        sb.AppendLine("else");
        sb.AppendLine($"  SQUID_BLUEGREEN_NEW_SLOT=\"{Green}\"; SQUID_BLUEGREEN_OLD_SLOT=\"{Blue}\"");
        sb.AppendLine("fi");
        sb.AppendLine("echo \"Blue/Green: active slot '${SQUID_BLUEGREEN_ACTIVE_SLOT:-none}', deploying to slot '$SQUID_BLUEGREEN_NEW_SLOT'\"");

        return sb.ToString();
    }

    /// <summary><c>--ignore-not-found</c>: only NotFound means "no Service"; any other lookup error aborts the script (<c>set -e</c>).</summary>
    private static string GenerateServiceSelectorProbe(ScriptPlan plan)
    {
        var jsonPath = $"{{.spec.selector.{KubernetesLabelKeys.DeploymentSlot.Replace(".", "\\.")}}}";
        var sb = new StringBuilder();

        sb.AppendLine($"SQUID_BLUEGREEN_SERVICE=\"$(kubectl get service \"{plan.ServiceName}\"{plan.NamespaceFlag} --ignore-not-found -o name)\"");
        sb.AppendLine("if [ -n \"$SQUID_BLUEGREEN_SERVICE\" ]; then");
        sb.AppendLine("  SQUID_BLUEGREEN_SERVICE_EXISTS=true");
        sb.AppendLine($"  SQUID_BLUEGREEN_ACTIVE_SLOT=\"$(kubectl get service \"{plan.ServiceName}\"{plan.NamespaceFlag} -o jsonpath='{jsonPath}')\"");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    /// <summary>
    /// Upgrade path: earlier releases left one slot Deployment behind and no slot selector. When
    /// exactly one slot exists it is the live one, so the first deploy already targets the other.
    /// </summary>
    private static string GenerateExistingSlotInference(ScriptPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("if [ -z \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" ]; then");
        sb.AppendLine($"  SQUID_BLUEGREEN_BLUE_FOUND=\"$(kubectl get deployment \"{BuildVersionedDeploymentName(plan.BaseName, Blue)}\"{plan.NamespaceFlag} --ignore-not-found -o name)\"");
        sb.AppendLine($"  SQUID_BLUEGREEN_GREEN_FOUND=\"$(kubectl get deployment \"{BuildVersionedDeploymentName(plan.BaseName, Green)}\"{plan.NamespaceFlag} --ignore-not-found -o name)\"");
        sb.AppendLine($"  if [ -n \"$SQUID_BLUEGREEN_GREEN_FOUND\" ] && [ -z \"$SQUID_BLUEGREEN_BLUE_FOUND\" ]; then SQUID_BLUEGREEN_ACTIVE_SLOT=\"{Green}\"; fi");
        sb.AppendLine($"  if [ -n \"$SQUID_BLUEGREEN_BLUE_FOUND\" ] && [ -z \"$SQUID_BLUEGREEN_GREEN_FOUND\" ]; then SQUID_BLUEGREEN_ACTIVE_SLOT=\"{Blue}\"; fi");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    private static string GeneratePinScript(ScriptPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: pin the Service to the live slot so the new slot takes no traffic during rollout.");
        sb.AppendLine("if [ \"$SQUID_BLUEGREEN_SERVICE_EXISTS\" = \"true\" ] && [ -n \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" ]; then");
        sb.AppendLine($"  echo \"Blue/Green: pinning Service {plan.ServiceName} to live slot '$SQUID_BLUEGREEN_ACTIVE_SLOT'\"");
        sb.AppendLine($"  {ServiceFunction} \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" | {plan.ApplyCommand}");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    private static string GenerateSlotApplyScript(ScriptPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: apply the Deployment for the new slot.");
        sb.AppendLine($"{DeploymentFunction} \"$SQUID_BLUEGREEN_NEW_SLOT\" | {plan.ApplyCommand}");

        return sb.ToString();
    }

    private static string GenerateRolloutWaitScript(ScriptPlan plan)
    {
        var deployment = $"deployment/{plan.BaseName}-$SQUID_BLUEGREEN_NEW_SLOT";
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: only switch traffic once the new slot has rolled out successfully.");
        sb.AppendLine($"echo \"Waiting for rollout: {deployment}\"");
        sb.AppendLine($"kubectl rollout status \"{deployment}\"{plan.NamespaceFlag} --timeout={plan.RolloutTimeoutSeconds}s");

        return sb.ToString();
    }

    private static string GenerateNoServiceNotice()
        => "echo \"Blue/Green: no Service configured, traffic switch and scale-down skipped\"\n";

    private static string GenerateSwitchScript(ScriptPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: switch traffic by applying the Service for the new slot.");
        sb.AppendLine($"{ServiceFunction} \"$SQUID_BLUEGREEN_NEW_SLOT\" | {plan.ApplyCommand}");
        sb.AppendLine($"echo \"Service {plan.ServiceName} selector switched to slot: $SQUID_BLUEGREEN_NEW_SLOT\"");

        return sb.ToString();
    }

    private static string GenerateScaleDownScript(ScriptPlan plan)
    {
        var oldDeployment = $"{plan.BaseName}-$SQUID_BLUEGREEN_OLD_SLOT";
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: scale down the old deployment after the switch.");
        sb.AppendLine($"SQUID_BLUEGREEN_OLD_DEPLOYMENT=\"$(kubectl get deployment \"{oldDeployment}\"{plan.NamespaceFlag} --ignore-not-found -o name)\"");
        sb.AppendLine("if [ -n \"$SQUID_BLUEGREEN_OLD_DEPLOYMENT\" ]; then");
        sb.AppendLine($"  kubectl scale deployment \"{oldDeployment}\"{plan.NamespaceFlag} --replicas=0");
        sb.AppendLine($"  echo \"Scaled down old deployment: {oldDeployment}\"");
        sb.AppendLine("else");
        sb.AppendLine($"  echo \"Old deployment {oldDeployment} not found, nothing to scale down\"");
        sb.AppendLine("fi");

        return sb.ToString();
    }
}
