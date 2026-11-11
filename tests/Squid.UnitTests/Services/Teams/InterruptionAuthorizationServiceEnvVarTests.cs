using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Teams;
using Squid.UnitTests.Support;

namespace Squid.UnitTests.Services.Teams;

/// <summary>
/// Proves the operator opt-out is actually READ from the process environment (the parser alone could be
/// correct yet never called). Mutates a process-global environment variable, so it runs serialised.
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public class InterruptionAuthorizationServiceEnvVarTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("off", false)]
    [InlineData("true", true)]
    public void AdministratorOverrideEnabled_IsResolvedFromTheEnvironmentVariable(string raw, bool expected)
    {
        var original = System.Environment.GetEnvironmentVariable(InterruptionAuthorizationService.AdministratorOverrideEnabledEnvVar);

        try
        {
            System.Environment.SetEnvironmentVariable(InterruptionAuthorizationService.AdministratorOverrideEnabledEnvVar, raw);

            var sut = new InterruptionAuthorizationService(new Mock<ITeamDataProvider>().Object, new Mock<IAuthorizationService>().Object, new Mock<IDeploymentDataProvider>().Object);

            sut.AdministratorOverrideEnabled.ShouldBe(expected);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(InterruptionAuthorizationService.AdministratorOverrideEnabledEnvVar, original);
        }
    }
}
