namespace Squid.IntegrationTests.Deployments.ServerTasks;

[Collection("ServerTask Tests")]
public class ServerTaskFixtureBase : TestBase
{
    protected ServerTaskFixtureBase() : base("_servertask_", "squid_test_servertask") { }
}
