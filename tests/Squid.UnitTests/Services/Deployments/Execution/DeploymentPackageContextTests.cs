using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Shouldly;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class DeploymentPackageContextTests
{
    private static List<ReleaseSelectedPackage> MakePackages(params (int feedId, string pkgId, string version)[] specs)
    {
        return specs.Select((s, i) => new ReleaseSelectedPackage
        {
            Id = i + 1,
            ReleaseId = 1,
            FeedId = s.feedId,
            ActionName = $"Action{i + 1}",
            PackageReferenceName = s.pkgId,
            Version = s.version
        }).ToList();
    }

    [Fact]
    public void Constructor_AllFields_SetCorrectly()
    {
        var packages = MakePackages((10, "nginx", "1.21.0"));
        var ctx = new DeploymentPackageContext(
            SelectedPackages: packages,
            PackageId: "nginx",
            PackageVersion: "1.21.0",
            PackageFeedId: 10,
            PackageSizeBytes: 12345,
            PackageHash: "abc123",
            PackageLocalPath: "/tmp/nginx.1.21.0.nupkg",
            PackageIndex: 0,
            PackageCount: 1,
            PackageTotalSizeBytes: 12345,
            PackageError: string.Empty);

        ctx.SelectedPackages.ShouldBeSameAs(packages);
        ctx.PackageId.ShouldBe("nginx");
        ctx.PackageVersion.ShouldBe("1.21.0");
        ctx.PackageFeedId.ShouldBe(10);
        ctx.PackageSizeBytes.ShouldBe(12345);
        ctx.PackageHash.ShouldBe("abc123");
        ctx.PackageLocalPath.ShouldBe("/tmp/nginx.1.21.0.nupkg");
        ctx.PackageIndex.ShouldBe(0);
        ctx.PackageCount.ShouldBe(1);
        ctx.PackageTotalSizeBytes.ShouldBe(12345);
        ctx.PackageError.ShouldBeEmpty();
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var packages = MakePackages((10, "nginx", "1.21.0"));

        var ctx1 = new DeploymentPackageContext(
            packages,
            "nginx", "1.21.0", 10, 12345, "abc123", "/tmp/a", 0, 1, 12345, string.Empty);

        var ctx2 = new DeploymentPackageContext(
            packages,
            "nginx", "1.21.0", 10, 12345, "abc123", "/tmp/a", 0, 1, 12345, string.Empty);

        ctx1.ShouldBe(ctx2);
    }

    [Fact]
    public void RecordEquality_DifferentPackageId_AreNotEqual()
    {
        var ctx1 = new DeploymentPackageContext(
            MakePackages((10, "nginx", "1.21.0")),
            "nginx", "1.21.0", 10, 12345, "abc123", "/tmp/a", 0, 1, 12345, string.Empty);

        var ctx2 = new DeploymentPackageContext(
            MakePackages((10, "redis", "1.21.0")),
            "redis", "1.21.0", 10, 12345, "abc123", "/tmp/a", 0, 1, 12345, string.Empty);

        ctx1.ShouldNotBe(ctx2);
    }

    [Fact]
    public void RecordEquality_DifferentFeedId_AreNotEqual()
    {
        var ctx1 = new DeploymentPackageContext(
            MakePackages((10, "nginx", "1.21.0")),
            "nginx", "1.21.0", 10, 12345, "abc123", "/tmp/a", 0, 1, 12345, string.Empty);

        var ctx2 = new DeploymentPackageContext(
            MakePackages((20, "nginx", "1.21.0")),
            "nginx", "1.21.0", 20, 12345, "abc123", "/tmp/a", 0, 1, 12345, string.Empty);

        ctx1.ShouldNotBe(ctx2);
    }

    [Fact]
    public void PackageCount_CorrectlySet()
    {
        var packages = MakePackages(
            (10, "nginx", "1.21.0"),
            (10, "redis", "7.0.0"),
            (20, "postgres", "15.0.0"));

        var ctx = new DeploymentPackageContext(
            SelectedPackages: packages,
            PackageId: string.Empty,
            PackageVersion: string.Empty,
            PackageFeedId: 0,
            PackageSizeBytes: 0,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 0,
            PackageCount: 3,
            PackageTotalSizeBytes: 0,
            PackageError: string.Empty);

        ctx.PackageCount.ShouldBe(3);
        ctx.SelectedPackages.Count.ShouldBe(3);
    }

    [Fact]
    public void PackageIndex_ZeroBased()
    {
        var packages = MakePackages((10, "pkg1", "1.0.0"), (10, "pkg2", "2.0.0"));

        for (var i = 0; i < packages.Count; i++)
        {
            var ctx = new DeploymentPackageContext(
                SelectedPackages: packages,
                PackageId: packages[i].PackageReferenceName,
                PackageVersion: packages[i].Version,
                PackageFeedId: packages[i].FeedId,
                PackageSizeBytes: 0,
                PackageHash: string.Empty,
                PackageLocalPath: string.Empty,
                PackageIndex: i,
                PackageCount: packages.Count,
                PackageTotalSizeBytes: 0,
                PackageError: string.Empty);

            ctx.PackageIndex.ShouldBe(i);
            ctx.PackageCount.ShouldBe(2);
        }
    }

    [Fact]
    public void PackageTotalSizeBytes_Cumulative()
    {
        var packages = MakePackages((10, "pkg1", "1.0.0"), (10, "pkg2", "2.0.0"));

        var ctx1 = new DeploymentPackageContext(
            SelectedPackages: packages,
            PackageId: "pkg1",
            PackageVersion: "1.0.0",
            PackageFeedId: 10,
            PackageSizeBytes: 100,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 0,
            PackageCount: 2,
            PackageTotalSizeBytes: 100,
            PackageError: string.Empty);

        var ctx2 = new DeploymentPackageContext(
            SelectedPackages: packages,
            PackageId: "pkg2",
            PackageVersion: "2.0.0",
            PackageFeedId: 10,
            PackageSizeBytes: 200,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 1,
            PackageCount: 2,
            PackageTotalSizeBytes: 300,
            PackageError: string.Empty);

        ctx1.PackageTotalSizeBytes.ShouldBe(100);
        ctx2.PackageTotalSizeBytes.ShouldBe(300);
    }

    [Fact]
    public void PackageError_ContainsMessage()
    {
        var ctx = new DeploymentPackageContext(
            SelectedPackages: MakePackages((0, "nginx", "1.21.0")),
            PackageId: "nginx",
            PackageVersion: "1.21.0",
            PackageFeedId: 0,
            PackageSizeBytes: 0,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 0,
            PackageCount: 1,
            PackageTotalSizeBytes: 0,
            PackageError: "Invalid FeedId: 0. FeedId must be a positive integer.");

        ctx.PackageError.ShouldContain("Invalid FeedId");
        ctx.PackageError.ShouldContain("0");
    }

    [Fact]
    public void EmptySelectedPackages_Allowed()
    {
        var ctx = new DeploymentPackageContext(
            SelectedPackages: new List<ReleaseSelectedPackage>(),
            PackageId: string.Empty,
            PackageVersion: string.Empty,
            PackageFeedId: 0,
            PackageSizeBytes: 0,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 0,
            PackageCount: 0,
            PackageTotalSizeBytes: 0,
            PackageError: string.Empty);

        ctx.SelectedPackages.ShouldBeEmpty();
        ctx.PackageCount.ShouldBe(0);
    }

    [Fact]
    public void RecordWithExpression_Body_DoesNotLeak()
    {
        // Verify the record is truly immutable — mutation via with-expression creates a new instance
        var packages = MakePackages((10, "nginx", "1.21.0"));
        var original = new DeploymentPackageContext(
            SelectedPackages: packages,
            PackageId: "nginx",
            PackageVersion: "1.21.0",
            PackageFeedId: 10,
            PackageSizeBytes: 0,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 0,
            PackageCount: 1,
            PackageTotalSizeBytes: 0,
            PackageError: string.Empty);

        var modified = original with { PackageSizeBytes = 999 };

        original.PackageSizeBytes.ShouldBe(0);
        modified.PackageSizeBytes.ShouldBe(999);
        modified.PackageId.ShouldBe("nginx"); // unchanged
    }

    [Fact]
    public void SelectedPackages_SameReference_NotCopied()
    {
        // The record should store the same list reference (not copy it)
        var packages = MakePackages((10, "nginx", "1.21.0"));
        var ctx = new DeploymentPackageContext(
            SelectedPackages: packages,
            PackageId: "nginx",
            PackageVersion: "1.21.0",
            PackageFeedId: 10,
            PackageSizeBytes: 0,
            PackageHash: string.Empty,
            PackageLocalPath: string.Empty,
            PackageIndex: 0,
            PackageCount: 1,
            PackageTotalSizeBytes: 0,
            PackageError: string.Empty);

        ctx.SelectedPackages.ShouldBeSameAs(packages);
    }
}
