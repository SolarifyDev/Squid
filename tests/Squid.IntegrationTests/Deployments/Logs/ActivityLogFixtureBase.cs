namespace Squid.IntegrationTests.Deployments.Logs;

[Collection("ActivityLog Tests")]
public class ActivityLogFixtureBase : TestBase
{
    protected ActivityLogFixtureBase() : base("_activitylog_", "squid_test_activitylog") { }
}
