using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — dispatch tests for <see cref="StructuredConfigFormatRegistry"/>.
/// Confirms each known extension lands on the right format + unknowns
/// return null (caller skips file with warning).
/// </summary>
public sealed class StructuredConfigFormatRegistryTests
{
    [Theory]
    [InlineData("/x/y.json", typeof(JsonConfigFormat))]
    [InlineData("/x/y.json5", typeof(JsonConfigFormat))]
    [InlineData("/x/y.JSON", typeof(JsonConfigFormat))]
    [InlineData("/x/y.yaml", typeof(YamlConfigFormat))]
    [InlineData("/x/y.yml", typeof(YamlConfigFormat))]
    [InlineData("/x/y.YML", typeof(YamlConfigFormat))]
    [InlineData("/x/y.xml", typeof(XmlConfigFormat))]
    [InlineData("/x/y.XML", typeof(XmlConfigFormat))]
    public void Resolve_KnownExtension_LandsOnExpectedFormat(string path, Type expectedType)
    {
        var format = StructuredConfigFormatRegistry.Resolve(path);
        format.ShouldNotBeNull();
        format.ShouldBeOfType(expectedType);
    }

    [Theory]
    [InlineData("/x/y.config")]    // XDT territory — deliberately NOT in this registry
    [InlineData("/x/y.txt")]
    [InlineData("/x/y.properties")]
    [InlineData("/x/y")]
    public void Resolve_UnknownExtension_ReturnsNull(string path)
    {
        StructuredConfigFormatRegistry.Resolve(path).ShouldBeNull();
    }

    [Fact]
    public void SupportedExtensions_ListsAllRegisteredFormats()
    {
        // Operator-facing list — used in skip-warning messages so the
        // operator knows which extensions the step works on.
        StructuredConfigFormatRegistry.SupportedExtensions
            .ShouldBe(new[] { ".json", ".json5", ".yaml", ".yml", ".xml" });
    }

    [Fact]
    public void FormatName_ExposedPerFormat_ForLogging()
    {
        // Used in `StructuredConfigVariables (YAML): 'foo.yaml' — ...` log lines.
        new JsonConfigFormat().FormatName.ShouldBe("JSON");
        new YamlConfigFormat().FormatName.ShouldBe("YAML");
        new XmlConfigFormat().FormatName.ShouldBe("XML");
    }
}
