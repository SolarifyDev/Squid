namespace Squid.IntegrationTests.Deployments.Snapshots;

[Collection("Snapshot Tests")]
public class SnapshotFixtureBase : TestBase
{
    protected SnapshotFixtureBase() : base("_snapshot_", "squid_test_snapshot") { }
}
