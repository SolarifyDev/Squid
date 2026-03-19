using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class CommunicationStyleParserTests
{
    [Theory]
    [InlineData("KubernetesApi", CommunicationStyle.KubernetesApi)]
    [InlineData("KubernetesAgent", CommunicationStyle.KubernetesAgent)]
    public void Parse_ValidPascalCaseProperty_ReturnsExpectedStyle(string styleValue, CommunicationStyle expected)
    {
        var json = $"{{\"CommunicationStyle\":\"{styleValue}\"}}";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("KubernetesApi", CommunicationStyle.KubernetesApi)]
    [InlineData("KubernetesAgent", CommunicationStyle.KubernetesAgent)]
    public void Parse_ValidCamelCaseProperty_ReturnsExpectedStyle(string styleValue, CommunicationStyle expected)
    {
        var json = $"{{\"communicationStyle\":\"{styleValue}\"}}";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("kubernetesapi", CommunicationStyle.KubernetesApi)]
    [InlineData("KUBERNETESAPI", CommunicationStyle.KubernetesApi)]
    [InlineData("kubernetesagent", CommunicationStyle.KubernetesAgent)]
    [InlineData("KUBERNETESAGENT", CommunicationStyle.KubernetesAgent)]
    public void Parse_CaseInsensitiveEnumValue_ReturnsExpectedStyle(string styleValue, CommunicationStyle expected)
    {
        var json = $"{{\"CommunicationStyle\":\"{styleValue}\"}}";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(expected);
    }

    [Fact]
    public void Parse_UnknownStyleValue_ReturnsUnknown()
    {
        var json = "{\"CommunicationStyle\":\"SshEndpoint\"}";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(CommunicationStyle.Unknown);
    }

    [Fact]
    public void Parse_MissingProperty_ReturnsUnknown()
    {
        var json = "{\"Endpoint\":\"https://cluster.local\"}";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(CommunicationStyle.Unknown);
    }

    [Fact]
    public void Parse_EmptyJsonObject_ReturnsUnknown()
    {
        var json = "{}";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(CommunicationStyle.Unknown);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsUnknown()
    {
        var json = "not valid json {{{";

        var result = CommunicationStyleParser.Parse(json);

        result.ShouldBe(CommunicationStyle.Unknown);
    }

    [Fact]
    public void Parse_NullInput_ReturnsUnknown()
    {
        var result = CommunicationStyleParser.Parse(null);

        result.ShouldBe(CommunicationStyle.Unknown);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsUnknown()
    {
        var result = CommunicationStyleParser.Parse(string.Empty);

        result.ShouldBe(CommunicationStyle.Unknown);
    }
}
