using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class EndpointVariableFactoryTests
{
    // === Make Tests ===

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Make_SetsAllFieldsCorrectly(bool isSensitive)
    {
        var variable = EndpointVariableFactory.Make("MyVar", "MyValue", isSensitive);

        variable.Name.ShouldBe("MyVar");
        variable.Value.ShouldBe("MyValue");
        variable.IsSensitive.ShouldBe(isSensitive);
        variable.Type.ShouldBe(VariableType.String);
        variable.Description.ShouldBe(string.Empty);
        variable.LastModifiedBy.ShouldBe(0);
    }

    // === TryDeserialize Tests ===

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryDeserialize_NullOrEmpty_ReturnsNull(string json)
    {
        var result = EndpointVariableFactory.TryDeserialize<TestDto>(json);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_InvalidJson_ReturnsNull()
    {
        var result = EndpointVariableFactory.TryDeserialize<TestDto>("not-valid-json{{");

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"Name":"hello","Value":42}""")]
    [InlineData("""{"name":"hello","value":42}""")]
    public void TryDeserialize_BothCasings_ReturnsDeserializedObject(string json)
    {
        var result = EndpointVariableFactory.TryDeserialize<TestDto>(json);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("hello");
        result.Value.ShouldBe(42);
    }

    private class TestDto
    {
        public string Name { get; set; }
        public int Value { get; set; }
    }
}
