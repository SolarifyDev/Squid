using System.Text.Json;
using Squid.Core.Services.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments.Account;

public class DeploymentAccountServiceTests
{
    // === SerializeEnvironmentIds ===

    [Fact]
    public void SerializeEnvironmentIds_WithValues_SerializesCorrectly()
    {
        var result = DeploymentAccountService.SerializeEnvironmentIds(new List<int> { 1, 3, 5 });

        result.ShouldBe("[1,3,5]");
    }

    [Fact]
    public void SerializeEnvironmentIds_EmptyList_ReturnsNull()
    {
        var result = DeploymentAccountService.SerializeEnvironmentIds(new List<int>());

        result.ShouldBeNull();
    }

    [Fact]
    public void SerializeEnvironmentIds_Null_ReturnsNull()
    {
        var result = DeploymentAccountService.SerializeEnvironmentIds(null);

        result.ShouldBeNull();
    }

    // === DeserializeEnvironmentIds ===

    [Fact]
    public void DeserializeEnvironmentIds_ValidJson_ReturnsIds()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds("[1,3,5]");

        result.ShouldBe(new List<int> { 1, 3, 5 });
    }

    [Fact]
    public void DeserializeEnvironmentIds_EmptyString_ReturnsEmptyList()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds(string.Empty);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DeserializeEnvironmentIds_Null_ReturnsEmptyList()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DeserializeEnvironmentIds_MalformedJson_ReturnsEmptyList()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds("not-json");

        result.ShouldBeEmpty();
    }
}
