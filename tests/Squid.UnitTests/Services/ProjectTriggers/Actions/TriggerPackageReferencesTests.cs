using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.ProjectTriggers.Actions;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.ProjectTriggers.Actions;

public class TriggerPackageReferencesTests
{
    [Fact]
    public void ReadsActionPackagesAndContainerAliases_AcceptsNumericAndStringFeedIds()
    {
        var properties = new Dictionary<string, string>
        {
            [SpecialVariables.Action.PackageFeedId] = "8",
            [SpecialVariables.Action.PackageId] = "charts/frontend",
            [KubernetesProperties.Containers] = """[{"Name":"api","PackageId":"images/api","FeedId":"9"},{"Name":"jobs","PackageId":"images/jobs","FeedId":9},{"Name":"sidecar","Image":"alpine:3.20"}]"""
        };

        var packages = TriggerPackageReferences.ReadAction("Install", properties);

        packages.ShouldBe(new[]
        {
            new TriggerPackageReference("Install", "charts/frontend", "charts/frontend", 8),
            new TriggerPackageReference("Install", "api", "images/api", 9),
            new TriggerPackageReference("Install", "jobs", "images/jobs", 9)
        }, ignoreOrder: true);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("""[{"Name":"api","PackageId":"images/api","FeedId":"#{unbound}"}]""")]
    [InlineData("""[{"Name":"api","PackageId":"images/api"}]""")]
    public void InvalidOrUnboundContainerReference_FailsBeforeCreatingRelease(string containers)
    {
        var properties = new Dictionary<string, string> { [KubernetesProperties.Containers] = containers };

        var error = Should.Throw<TriggerActionRejectedException>(() => TriggerPackageReferences.ReadAction("private-action", properties));
        error.Message.ShouldNotContain("private-action");
        error.Message.ShouldNotContain(containers);
    }

    [Fact]
    public void PackageDeploymentWithNoBoundFeedOrPackage_FailsBeforeCreatingRelease()
    {
        Should.Throw<InvalidOperationException>(() => TriggerPackageReferences.ReadAction("Install", new Dictionary<string, string>(), SpecialVariables.ActionTypes.TentaclePackage));
    }

    [Fact]
    public void SnapshotMustStillContainExactlyThePreparedPackageReferences()
    {
        var properties = new Dictionary<string, string> { [SpecialVariables.Action.PackageFeedId] = "8", [SpecialVariables.Action.PackageId] = "changed" };
        var snapshot = new Squid.Message.Models.Deployments.Snapshots.DeploymentProcessSnapshotDto
        {
            Data = new() { StepSnapshots = [new() { ActionSnapshots = [new() { Name = "Deploy", Properties = properties }] }] }
        };
        var fingerprint = TriggerPackageReferences.Fingerprint([new("Deploy", "original", "original", 8)]);

        Should.Throw<InvalidOperationException>(() => TriggerPackageReferences.ValidateSnapshot(snapshot, 1, fingerprint));
        Should.Throw<InvalidOperationException>(() => TriggerPackageReferences.ValidateSnapshot(snapshot, 1, null));
    }

    [Fact]
    public void IdentityDigest_RejectsImageSwapsUnderAnUnchangedAlias_AndIgnoresEnumerationOrder()
    {
        var api = new TriggerPackageReference("Deploy", "api", "team/api", 8);
        var jobs = new TriggerPackageReference("Deploy", "jobs", "team/jobs", 8);

        var original = TriggerPackageReferences.Fingerprint([api, jobs]);

        TriggerPackageReferences.Fingerprint([jobs, api]).ShouldBe(original);
        TriggerPackageReferences.Fingerprint([api with { PackageId = "other/api" }, jobs]).ShouldNotBe(original);
    }
}
