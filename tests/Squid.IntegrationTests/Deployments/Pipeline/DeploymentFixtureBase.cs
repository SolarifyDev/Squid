namespace Squid.IntegrationTests.Deployments.Pipeline;

[Collection("Deployment Tests")]
public class DeploymentFixtureBase : TestBase
{
    protected DeploymentFixtureBase() : base("_deployment_", "squid_test_deployment") { }
}
