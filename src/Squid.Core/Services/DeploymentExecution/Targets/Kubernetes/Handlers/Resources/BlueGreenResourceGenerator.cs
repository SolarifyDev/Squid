using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

/// <summary>
/// Generates Blue/Green deployment resources:
/// 1. A versioned Deployment with a slot label (blue or green)
/// 2. A script to patch the Service selector to the new slot
/// 3. A script to scale down the old deployment
///
/// Slot assignment alternates: if the current active slot is "blue", the new slot is "green" and vice versa.
/// The slot is determined from a property (defaulting to "green" for the initial deployment).
/// </summary>
internal sealed class BlueGreenResourceGenerator
{
    private readonly DeploymentResourceGenerator _deploymentGenerator = new();

    internal static string ResolveNewSlot(string? currentSlot)
    {
        return string.Equals(currentSlot, "green", StringComparison.OrdinalIgnoreCase)
            ? "blue"
            : "green";
    }

    internal static string BuildVersionedDeploymentName(string baseName, string slot)
        => $"{baseName}-{slot}";

    internal static string BuildOldDeploymentName(string baseName, string newSlot)
    {
        var oldSlot = string.Equals(newSlot, "green", StringComparison.OrdinalIgnoreCase) ? "blue" : "green";
        return $"{baseName}-{oldSlot}";
    }

    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var style = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentStyle);
        return string.Equals(style, KubernetesDeploymentStrategyValues.BlueGreen, StringComparison.OrdinalIgnoreCase)
               && _deploymentGenerator.CanGenerate(properties);
    }

    public Dictionary<string, string> GenerateAll(Dictionary<string, string> properties)
    {
        var result = new Dictionary<string, string>();

        var baseName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);
        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var currentSlot = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.BlueGreenActiveSlot);
        var newSlot = ResolveNewSlot(currentSlot);
        var versionedName = BuildVersionedDeploymentName(baseName, newSlot);
        var oldName = BuildOldDeploymentName(baseName, newSlot);

        var deploymentProps = new Dictionary<string, string>(properties);
        deploymentProps[KubernetesProperties.DeploymentName] = versionedName;
        deploymentProps[KubernetesProperties.DeploymentStyle] = KubernetesDeploymentStrategyValues.RollingUpdate;

        InjectSlotLabel(deploymentProps, newSlot);

        result["deployment.yaml"] = _deploymentGenerator.Generate(deploymentProps);
        result["bluegreen-switch.sh"] = GenerateSwitchScript(baseName, namespaceName, newSlot);
        result["bluegreen-scaledown.sh"] = GenerateScaleDownScript(oldName, namespaceName);

        return result;
    }

    private static void InjectSlotLabel(Dictionary<string, string> properties, string slot)
    {
        var existingLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);
        existingLabels[KubernetesLabelKeys.DeploymentSlot] = slot;

        var labelsJson = System.Text.Json.JsonSerializer.Serialize(existingLabels);
        properties[KubernetesProperties.DeploymentLabels] = labelsJson;
    }

    internal static string GenerateSwitchScript(string serviceName, string namespaceName, string newSlot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine("# Blue/Green: patch the Service selector to point to the new deployment slot.");

        var nsFlag = string.IsNullOrWhiteSpace(namespaceName) ? "" : $" -n \"{namespaceName}\"";

        sb.AppendLine($"kubectl patch service \"{serviceName}\"{nsFlag} --type='json' \\");
        sb.AppendLine($"  -p='[{{\"op\": \"add\", \"path\": \"/spec/selector/{KubernetesLabelKeys.DeploymentSlot.Replace("/", "~1")}\", \"value\": \"{newSlot}\"}}]'");
        sb.AppendLine();
        sb.AppendLine($"echo \"Service {serviceName} selector patched to slot: {newSlot}\"");

        return sb.ToString();
    }

    internal static string GenerateScaleDownScript(string oldDeploymentName, string namespaceName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine("# Blue/Green: scale down the old deployment after the switch.");

        var nsFlag = string.IsNullOrWhiteSpace(namespaceName) ? "" : $" -n \"{namespaceName}\"";

        sb.AppendLine($"if kubectl get deployment \"{oldDeploymentName}\"{nsFlag} > /dev/null 2>&1; then");
        sb.AppendLine($"  kubectl scale deployment \"{oldDeploymentName}\"{nsFlag} --replicas=0");
        sb.AppendLine($"  echo \"Scaled down old deployment: {oldDeploymentName}\"");
        sb.AppendLine("else");
        sb.AppendLine($"  echo \"Old deployment {oldDeploymentName} not found, nothing to scale down\"");
        sb.AppendLine("fi");

        return sb.ToString();
    }
}
