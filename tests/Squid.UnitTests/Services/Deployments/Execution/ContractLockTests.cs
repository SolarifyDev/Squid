using System.Linq;
using System.Reflection;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Phase 10 contract lock — captures the public surface of the core deployment-execution
/// abstractions after the genericity refactor. A new member on any of these types is an
/// intentional architectural change: update the expected-member list in this file and
/// keep the commit scoped so the delta is reviewable.
/// </summary>
public class ContractLockTests
{
    [Fact]
    public void IDeploymentTransport_PublicSurface_IsLocked()
    {
        var members = GetPublicMemberNames(typeof(IDeploymentTransport));

        members.ShouldBe(new[]
        {
            "Capabilities",
            "CommunicationStyle",
            "HealthChecker",
            "Strategy",
            "Variables"
        }, ignoreOrder: false);
    }

    [Fact]
    public void IDeploymentTransport_DoesNotExposeLegacyForwarders()
    {
        var members = GetPublicMemberNames(typeof(IDeploymentTransport));

        members.ShouldNotContain("ExecutionLocation");
        members.ShouldNotContain("ExecutionBackend");
        members.ShouldNotContain("RequiresContextPreparationForPackagedPayload");
    }

    [Fact]
    public void IIntentRenderer_PublicSurface_IsLocked()
    {
        var members = GetPublicMemberNames(typeof(IIntentRenderer));

        members.ShouldBe(new[]
        {
            "CanRender",
            "CommunicationStyle",
            "RenderAsync"
        }, ignoreOrder: false);
    }

    [Fact]
    public void ITransportCapabilities_PublicSurface_IsLocked()
    {
        var members = GetPublicMemberNames(typeof(ITransportCapabilities));

        members.ShouldBe(new[]
        {
            "ExecutionBackend",
            "ExecutionLocation",
            "MaxFileSizeBytes",
            "OptionalFeatures",
            "PackageStagingModes",
            "RequiresContextPreparationForPackagedPayload",
            "SupportedActionTypes",
            "SupportedSyntaxes",
            "SupportsArtifactCollection",
            "SupportsExecutableFlag",
            "SupportsIsolationMutex",
            "SupportsNestedFiles",
            "SupportsOutputVariables",
            "SupportsSudo"
        }, ignoreOrder: false);
    }

    [Fact]
    public void ExecutionIntent_ConcreteSubtypes_AreLocked()
    {
        var subtypes = typeof(ExecutionIntent).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(ExecutionIntent).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        subtypes.ShouldBe(new[]
        {
            "DeployPackageIntent",
            "HealthCheckIntent",
            "HelmUpgradeIntent",
            "KubernetesApplyIntent",
            "KubernetesKustomizeIntent",
            "ManualInterventionIntent",
            "OpenClawInvokeIntent",
            "RunScriptIntent"
        }, ignoreOrder: false);
    }

    [Fact]
    public void ExecutionIntent_BaseFields_AreLocked()
    {
        var members = GetPublicMemberNames(typeof(ExecutionIntent));

        members.ShouldContain("Name");
        members.ShouldContain("StepName");
        members.ShouldContain("ActionName");
        members.ShouldContain("Assets");
        members.ShouldContain("RequiredCapabilities");
        members.ShouldContain("Packages");
        members.ShouldContain("Timeout");
    }

    private static string[] GetPublicMemberNames(Type type)
    {
        return type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !IsCompilerGenerated(m))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsCompilerGenerated(MemberInfo member)
    {
        if (member is MethodInfo method)
        {
            if (method.IsSpecialName) return true;
            if (method.Name is "Equals" or "GetHashCode" or "ToString" or "GetType") return true;
            if (method.Name.StartsWith("<", StringComparison.Ordinal)) return true;
            if (method.Name is "Deconstruct" or "PrintMembers" or "<Clone>$") return true;
            if (method.DeclaringType == typeof(object)) return true;
        }

        if (member.Name == "EqualityContract") return true;

        return false;
    }
}
