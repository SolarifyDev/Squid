using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account.Exceptions;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Services.Deployments.Account;

public class AccountEnvironmentScopeTests
{
    [Fact]
    public void NullEnvironmentIds_AllowsAnyEnvironment()
    {
        var account = new DeploymentAccount { Id = 1, Name = "Test", EnvironmentIds = null };
        var environment = new Environment { Id = 99 };

        Should.NotThrow(() => PrepareTargetsPhase.ValidateAccountEnvironmentScope(account, environment));
    }

    [Fact]
    public void EmptyArray_AllowsAnyEnvironment()
    {
        var account = new DeploymentAccount { Id = 1, Name = "Test", EnvironmentIds = "[]" };
        var environment = new Environment { Id = 99 };

        Should.NotThrow(() => PrepareTargetsPhase.ValidateAccountEnvironmentScope(account, environment));
    }

    [Fact]
    public void ScopedToMatchingEnvironment_Succeeds()
    {
        var account = new DeploymentAccount { Id = 1, Name = "Test", EnvironmentIds = JsonSerializer.Serialize(new List<int> { 1, 3, 5 }) };
        var environment = new Environment { Id = 3 };

        Should.NotThrow(() => PrepareTargetsPhase.ValidateAccountEnvironmentScope(account, environment));
    }

    [Fact]
    public void ScopedToNonMatchingEnvironment_ThrowsAccountEnvironmentScopeException()
    {
        var account = new DeploymentAccount { Id = 42, Name = "Prod-Only", EnvironmentIds = JsonSerializer.Serialize(new List<int> { 1, 3 }) };
        var environment = new Environment { Id = 99 };

        var ex = Should.Throw<AccountEnvironmentScopeException>(
            () => PrepareTargetsPhase.ValidateAccountEnvironmentScope(account, environment));

        ex.AccountId.ShouldBe(42);
        ex.AccountName.ShouldBe("Prod-Only");
        ex.EnvironmentId.ShouldBe(99);
    }

    [Fact]
    public void MalformedJson_TreatedAsUnscoped()
    {
        var account = new DeploymentAccount { Id = 1, Name = "Test", EnvironmentIds = "not-valid-json" };
        var environment = new Environment { Id = 99 };

        Should.NotThrow(() => PrepareTargetsPhase.ValidateAccountEnvironmentScope(account, environment));
    }
}
