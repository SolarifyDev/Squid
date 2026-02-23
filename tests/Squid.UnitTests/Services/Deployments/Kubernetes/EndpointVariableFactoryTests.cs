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
        variable.LastModifiedBy.ShouldBe("System");
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

    [Fact]
    public void TryDeserialize_ValidJson_ReturnsDeserializedObject()
    {
        var json = """{"Name":"hello","Value":42}""";

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
