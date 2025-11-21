using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesIngressActionYamlGenerator : IActionYamlGenerator
{
    private const string IngressActionType = "Octopus.KubernetesDeployIngress";

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null)
        {
            return false;
        }

        return string.Equals(action.ActionType, IngressActionType, StringComparison.OrdinalIgnoreCase);
    }

    public Task<Dictionary<string, byte[]>> GenerateAsync(
        DeploymentStepDto step,
        DeploymentActionDto action,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>();

        if (!CanHandle(action))
        {
            return Task.FromResult(result);
        }

        var properties = BuildPropertyDictionary(action);

        cancellationToken.ThrowIfCancellationRequested();

        var ingressYaml = GenerateIngressYaml(properties);

        if (!string.IsNullOrWhiteSpace(ingressYaml))
        {
            result["ingress.yaml"] = Encoding.UTF8.GetBytes(ingressYaml);
        }

        return Task.FromResult(result);
    }

    private static Dictionary<string, string> BuildPropertyDictionary(DeploymentActionDto action)
    {
        if (action.Properties == null || action.Properties.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, string>(action.Properties.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in action.Properties)
        {
            dict[prop.PropertyName] = prop.PropertyValue;
        }

        return dict;
    }

    private string GenerateIngressYaml(Dictionary<string, string> properties)
    {
        var ingressName = GetProperty(properties, "Octopus.Action.KubernetesContainers.IngressName");

        if (string.IsNullOrWhiteSpace(ingressName))
        {
            ingressName = "ingress";
        }

        var namespaceName = GetNamespace(properties);

        var annotationsJson = GetProperty(properties, "Octopus.Action.KubernetesContainers.IngressAnnotations");

        var rulesJson = GetProperty(properties, "Octopus.Action.KubernetesContainers.IngressRules");

        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return string.Empty;
        }

        var tlsJson = GetProperty(properties, "Octopus.Action.KubernetesContainers.IngressTlsCertificates");

        var ingressClassName = GetProperty(properties, "Octopus.Action.KubernetesContainers.IngressClassName");

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: networking.k8s.io/v1");
        sb.AppendLine("kind: Ingress");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {IngressNameOrDefault(ingressName)}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            sb.AppendLine($"  namespace: {namespaceName}");
        }

        if (!string.IsNullOrWhiteSpace(annotationsJson))
        {
            sb.AppendLine("  annotations: " + annotationsJson);
        }

        sb.AppendLine("spec:");

        if (!string.IsNullOrWhiteSpace(ingressClassName))
        {
            sb.AppendLine($"  ingressClassName: {ingressClassName}");
        }

        sb.AppendLine("  rules: " + rulesJson);

        if (!string.IsNullOrWhiteSpace(tlsJson))
        {
            sb.AppendLine("  tls: " + tlsJson);
        }

        return sb.ToString();
    }

    private static string GetNamespace(Dictionary<string, string> properties)
    {
        var ns = GetProperty(properties, "Octopus.Action.Kubernetes.Namespace");

        if (string.IsNullOrWhiteSpace(ns))
        {
            ns = "default";
        }

        return ns;
    }

    private static string GetProperty(Dictionary<string, string> properties, string name)
    {
        if (properties.TryGetValue(name, out var value))
        {
            return value;
        }

        return string.Empty;
    }

    private static string IngressNameOrDefault(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "ingress";
        }

        return name;
    }
}

