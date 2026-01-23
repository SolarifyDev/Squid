using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.IntegrationTests.Builders;

public class KubernetesTestDataBuilder
{
    private readonly Dictionary<string, string> _actionProperties = new();

    public KubernetesTestDataBuilder WithDeploymentName(string name)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.DeploymentName"] = name;
        return this;
    }

    public KubernetesTestDataBuilder WithNamespace(string ns)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.Namespace"] = ns;
        return this;
    }

    public KubernetesTestDataBuilder WithReplicas(int count)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.Replicas"] = count.ToString();
        return this;
    }

    public KubernetesTestDataBuilder WithDeploymentStyle(string style)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.DeploymentStyle"] = style;
        return this;
    }

    public KubernetesTestDataBuilder WithService(string name, string type = "ClusterIP", string? ports = null)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.ServiceName"] = name;
        _actionProperties["Octopus.Action.KubernetesContainers.ServiceType"] = type;
        
        if (ports != null)
        {
            _actionProperties["Octopus.Action.KubernetesContainers.ServicePorts"] = ports;
        }
        
        return this;
    }

    public KubernetesTestDataBuilder WithContainer(string name, string image, int port, Action<ContainerConfig>? configure = null)
    {
        var config = new ContainerConfig
        {
            Name = name,
            Image = image,
            Port = port
        };
        configure?.Invoke(config);

        var containers = JsonSerializer.Serialize(new[]
        {
            new
            {
                Name = config.Name,
                Image = config.Image,
                Ports = new[] { new { key = "http", value = config.Port.ToString(), option = "TCP" } },
                Resources = config.Resources,
                VolumeMounts = config.VolumeMounts,
                ConfigMapEnvFromSource = config.ConfigMapEnvFromSource
            }
        });
        _actionProperties["Octopus.Action.KubernetesContainers.Containers"] = containers;
        return this;
    }

    public KubernetesTestDataBuilder WithServicePorts(string portsJson)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.ServicePorts"] = portsJson;
        return this;
    }

    public KubernetesTestDataBuilder WithCombinedVolumes(string volumesJson)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.CombinedVolumes"] = volumesJson;
        return this;
    }

    public KubernetesTestDataBuilder WithConfigMap(string name, Dictionary<string, string> values)
    {
        _actionProperties["Octopus.Action.KubernetesContainers.ConfigMapName"] = name;
        _actionProperties["Octopus.Action.KubernetesContainers.ConfigMapValues"] = JsonSerializer.Serialize(values);
        return this;
    }

    public (DeploymentStepDto Step, DeploymentActionDto Action) Build(string stepName = "Deploy", string actionName = "Deploy")
    {
        var step = new DeploymentStepDto
        {
            Id = 1,
            Name = stepName,
            StepType = "Deploy"
        };

        var action = new DeploymentActionDto
        {
            Id = 1,
            StepId = step.Id,
            Name = actionName,
            ActionType = "Octopus.KubernetesDeployContainers"
        };

        foreach (var prop in _actionProperties)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = prop.Key,
                PropertyValue = prop.Value
            });
        }

        return (step, action);
    }

    public async Task<Dictionary<string, byte[]>> GenerateYamlAsync(
        KubernetesContainersActionYamlGenerator generator,
        string stepName = "Deploy",
        string actionName = "Deploy")
    {
        var (step, action) = Build(stepName, actionName);
        return await generator.GenerateAsync(step, action, CancellationToken.None);
    }
}

public class ContainerConfig
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public int Port { get; set; }
    public object Resources { get; set; } = new { requests = new { cpu = "100m", memory = "256Mi" }, limits = new { cpu = "500m", memory = "512Mi" } };
    public List<object> VolumeMounts { get; set; } = new();
    public List<object> ConfigMapEnvFromSource { get; set; } = new();
}
