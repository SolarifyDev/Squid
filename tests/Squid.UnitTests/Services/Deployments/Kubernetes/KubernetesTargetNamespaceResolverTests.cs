using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Unit coverage for the single source of truth that resolves a Kubernetes target
/// namespace from action variables at render time. Pins the behaviour that moved
/// out of the generic <c>ExecuteStepsPhase</c>: read
/// <see cref="SpecialVariables.Kubernetes.Namespace"/>, expand <c>#{...}</c> templates,
/// and honour project-overrides-endpoint precedence (first occurrence wins).
/// </summary>
public class KubernetesTargetNamespaceResolverTests
{
    [Fact]
    public void Resolve_LiteralNamespaceVariable_ReturnedAsIs()
    {
        var context = ContextWith(new VariableDto { Name = SpecialVariables.Kubernetes.Namespace, Value = "production" });

        KubernetesTargetNamespaceResolver.Resolve(context).ShouldBe("production");
    }

    [Fact]
    public void Resolve_TemplateNamespaceVariable_ExpandedThroughDictionary()
    {
        var context = ContextWith(
            new VariableDto { Name = "Environment", Value = "prod" },
            new VariableDto { Name = SpecialVariables.Kubernetes.Namespace, Value = "#{Environment}-app" });

        KubernetesTargetNamespaceResolver.Resolve(context).ShouldBe("prod-app");
    }

    [Fact]
    public void Resolve_TwoNamespaceVariables_FirstWins()
    {
        // effectiveVariables lists project vars before endpoint vars, so the first
        // occurrence is the project override — it must win.
        var context = ContextWith(
            new VariableDto { Name = SpecialVariables.Kubernetes.Namespace, Value = "project-ns" },
            new VariableDto { Name = SpecialVariables.Kubernetes.Namespace, Value = "endpoint-ns" });

        KubernetesTargetNamespaceResolver.Resolve(context).ShouldBe("project-ns");
    }

    [Fact]
    public void Resolve_AbsentNamespaceVariable_ReturnsNull()
    {
        var context = ContextWith(new VariableDto { Name = "Unrelated", Value = "x" });

        KubernetesTargetNamespaceResolver.Resolve(context).ShouldBeNull();
    }

    [Fact]
    public void Resolve_EmptyNamespaceVariable_ReturnsEmpty()
    {
        // Empty is returned untouched (no expansion attempt); the renderer decides the default.
        var context = ContextWith(new VariableDto { Name = SpecialVariables.Kubernetes.Namespace, Value = "" });

        KubernetesTargetNamespaceResolver.Resolve(context).ShouldBe("");
    }

    private static IntentRenderContext ContextWith(params VariableDto[] variables)
    {
        var list = variables.ToList();

        return new IntentRenderContext
        {
            Target = new DeploymentTargetContext
            {
                Machine = new Machine { Id = 1, Name = "m1" },
                CommunicationStyle = CommunicationStyle.KubernetesAgent,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "step-1" },
            EffectiveVariables = list,
            VariableDictionary = VariableDictionaryFactory.Create(list)
        };
    }
}
