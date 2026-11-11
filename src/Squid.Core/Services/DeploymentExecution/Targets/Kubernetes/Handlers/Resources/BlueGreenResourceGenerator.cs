using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

/// <summary>
/// Generates the Blue/Green runtime script (<see cref="ScriptFileName"/>). The script runs on
/// the target after the other manifests are applied and:
/// 1. Derives the live slot from the Service selector label <c>squid.io/deployment-slot</c>
///    (falling back to the <c>BlueGreenActiveSlot</c> property as a seed), so the cluster is the
///    single source of truth and every target / namespace alternates independently
/// 2. Applies the Deployment for the other slot (both slot manifests are embedded)
/// 3. Waits for its rollout to succeed
/// 4. Patches the Service selector to the new slot
/// 5. Scales the old slot's Deployment to zero
///
/// When the Service does not exist there is no traffic to switch: the new slot is applied and
/// rolled out, and the switch and scale-down are skipped.
/// </summary>
internal sealed class BlueGreenResourceGenerator
{
    internal const string ScriptFileName = "bluegreen.sh";

    private const string Blue = "blue";
    private const string Green = "green";
    private const string ManifestDelimiter = "SQUID_BLUEGREEN_MANIFEST";
    private const int DefaultRolloutTimeoutSeconds = 300;

    private readonly DeploymentResourceGenerator _deploymentGenerator = new();

    private sealed record ScriptPlan(string BaseName, string ServiceName, string NamespaceName, string SeedActiveSlot, string ApplyCommand, int RolloutTimeoutSeconds);

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
        var blueManifest = GenerateSlotManifest(properties, Blue);
        var greenManifest = GenerateSlotManifest(properties, Green);

        return new Dictionary<string, string> { [ScriptFileName] = GenerateScript(plan, blueManifest, greenManifest) };
    }

    private static ScriptPlan BuildPlan(Dictionary<string, string> properties)
    {
        var baseName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);
        var serviceName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceName);

        return new ScriptPlan(
            baseName,
            string.IsNullOrWhiteSpace(serviceName) ? baseName : serviceName,
            KubernetesPropertyParser.GetNamespace(properties),
            ReadSeedActiveSlot(properties),
            BuildApplyCommand(properties),
            ReadRolloutTimeoutSeconds(properties));
    }

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

    private string GenerateSlotManifest(Dictionary<string, string> properties, string slot)
    {
        var baseName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);

        var deploymentProps = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
        deploymentProps[KubernetesProperties.DeploymentName] = BuildVersionedDeploymentName(baseName, slot);
        deploymentProps[KubernetesProperties.DeploymentStyle] = KubernetesDeploymentStrategyValues.RollingUpdate;

        InjectSlotLabel(deploymentProps, baseName, slot);

        return _deploymentGenerator.Generate(deploymentProps);
    }

    /// <summary>
    /// Adds the slot label. When no labels are configured the Service selector falls back to
    /// <c>app: {baseName}</c>, so that label is seeded too — otherwise no slot's pods would match.
    /// </summary>
    private static void InjectSlotLabel(Dictionary<string, string> properties, string baseName, string slot)
    {
        var labels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);

        if (labels.Count == 0)
            labels[KubernetesLabelKeys.App] = baseName;

        labels[KubernetesLabelKeys.DeploymentSlot] = slot;

        properties[KubernetesProperties.DeploymentLabels] = System.Text.Json.JsonSerializer.Serialize(labels);
    }

    private static string GenerateScript(ScriptPlan plan, string blueManifest, string greenManifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.Append(GenerateSlotDetectionScript(plan));
        sb.AppendLine();
        sb.Append(GenerateSlotApplyScript(plan, blueManifest, greenManifest));
        sb.AppendLine();
        sb.Append(GenerateRolloutWaitScript(plan));
        sb.AppendLine();
        sb.Append(GenerateMissingServiceGuard(plan));
        sb.AppendLine();
        sb.Append(GenerateSwitchScript(plan.ServiceName, plan.NamespaceName));
        sb.AppendLine();
        sb.Append(GenerateScaleDownScript(plan.BaseName, plan.NamespaceName));

        return sb.ToString();
    }

    private static string GenerateSlotDetectionScript(ScriptPlan plan)
    {
        var jsonPath = $"{{.spec.selector.{KubernetesLabelKeys.DeploymentSlot.Replace(".", "\\.")}}}";
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: derive the live slot from the Service selector (cluster is the source of truth).");
        sb.AppendLine("SQUID_BLUEGREEN_SERVICE_EXISTS=false");
        sb.AppendLine("SQUID_BLUEGREEN_LIVE_SLOT=\"\"");
        sb.AppendLine($"if kubectl get service \"{plan.ServiceName}\"{BuildNamespaceFlag(plan.NamespaceName)} > /dev/null 2>&1; then");
        sb.AppendLine("  SQUID_BLUEGREEN_SERVICE_EXISTS=true");
        sb.AppendLine($"  SQUID_BLUEGREEN_LIVE_SLOT=\"$(kubectl get service \"{plan.ServiceName}\"{BuildNamespaceFlag(plan.NamespaceName)} -o jsonpath='{jsonPath}')\"");
        sb.AppendLine("fi");
        sb.AppendLine($"SQUID_BLUEGREEN_ACTIVE_SLOT=\"{plan.SeedActiveSlot}\"");
        sb.AppendLine("if [ -n \"$SQUID_BLUEGREEN_LIVE_SLOT\" ]; then SQUID_BLUEGREEN_ACTIVE_SLOT=\"$SQUID_BLUEGREEN_LIVE_SLOT\"; fi");
        sb.AppendLine($"if [ \"$SQUID_BLUEGREEN_ACTIVE_SLOT\" = \"{Green}\" ]; then");
        sb.AppendLine($"  SQUID_BLUEGREEN_NEW_SLOT=\"{Blue}\"; SQUID_BLUEGREEN_OLD_SLOT=\"{Green}\"");
        sb.AppendLine("else");
        sb.AppendLine($"  SQUID_BLUEGREEN_NEW_SLOT=\"{Green}\"; SQUID_BLUEGREEN_OLD_SLOT=\"{Blue}\"");
        sb.AppendLine("fi");
        sb.AppendLine("echo \"Blue/Green: active slot '${SQUID_BLUEGREEN_ACTIVE_SLOT:-none}', deploying to slot '$SQUID_BLUEGREEN_NEW_SLOT'\"");

        return sb.ToString();
    }

    private static string GenerateSlotApplyScript(ScriptPlan plan, string blueManifest, string greenManifest)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: apply the Deployment for the new slot.");
        sb.AppendLine("case \"$SQUID_BLUEGREEN_NEW_SLOT\" in");
        AppendSlotCase(sb, Blue, plan.ApplyCommand, blueManifest);
        AppendSlotCase(sb, Green, plan.ApplyCommand, greenManifest);
        sb.AppendLine("esac");

        return sb.ToString();
    }

    private static void AppendSlotCase(StringBuilder sb, string slot, string applyCommand, string manifest)
    {
        sb.AppendLine($"  {slot})");
        sb.AppendLine($"{applyCommand} <<'{ManifestDelimiter}'");
        sb.AppendLine(manifest.TrimEnd());
        sb.AppendLine(ManifestDelimiter);
        sb.AppendLine("    ;;");
    }

    private static string GenerateRolloutWaitScript(ScriptPlan plan)
    {
        var deployment = $"deployment/{plan.BaseName}-$SQUID_BLUEGREEN_NEW_SLOT";
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: only switch traffic once the new slot has rolled out successfully.");
        sb.AppendLine($"echo \"Waiting for rollout: {deployment}\"");
        sb.AppendLine($"kubectl rollout status \"{deployment}\"{BuildNamespaceFlag(plan.NamespaceName)} --timeout={plan.RolloutTimeoutSeconds}s");

        return sb.ToString();
    }

    private static string GenerateMissingServiceGuard(ScriptPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("if [ \"$SQUID_BLUEGREEN_SERVICE_EXISTS\" != \"true\" ]; then");
        sb.AppendLine($"  echo \"Blue/Green: Service {plan.ServiceName} not found, skipping traffic switch and scale-down\"");
        sb.AppendLine("  exit 0");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    internal static string GenerateSwitchScript(string serviceName, string namespaceName)
    {
        var namespaceFlag = BuildNamespaceFlag(namespaceName);
        var path = $"/spec/selector/{KubernetesLabelKeys.DeploymentSlot.Replace("/", "~1")}";
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: patch the Service selector to point to the new deployment slot.");
        sb.AppendLine($"kubectl patch service \"{serviceName}\"{namespaceFlag} --type='json' \\");
        sb.AppendLine($"  -p='[{{\"op\": \"add\", \"path\": \"{path}\", \"value\": \"'\"$SQUID_BLUEGREEN_NEW_SLOT\"'\"}}]'");
        sb.AppendLine($"echo \"Service {serviceName} selector patched to slot: $SQUID_BLUEGREEN_NEW_SLOT\"");

        return sb.ToString();
    }

    internal static string GenerateScaleDownScript(string baseName, string namespaceName)
    {
        var namespaceFlag = BuildNamespaceFlag(namespaceName);
        var oldDeployment = $"{baseName}-$SQUID_BLUEGREEN_OLD_SLOT";
        var sb = new StringBuilder();

        sb.AppendLine("# Blue/Green: scale down the old deployment after the switch.");
        sb.AppendLine($"if kubectl get deployment \"{oldDeployment}\"{namespaceFlag} > /dev/null 2>&1; then");
        sb.AppendLine($"  kubectl scale deployment \"{oldDeployment}\"{namespaceFlag} --replicas=0");
        sb.AppendLine($"  echo \"Scaled down old deployment: {oldDeployment}\"");
        sb.AppendLine("else");
        sb.AppendLine($"  echo \"Old deployment {oldDeployment} not found, nothing to scale down\"");
        sb.AppendLine("fi");

        return sb.ToString();
    }
}
