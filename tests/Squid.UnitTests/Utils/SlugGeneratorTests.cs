using Squid.Core.Utils;

namespace Squid.UnitTests.Utils;

public class SlugGeneratorTests
{
    [Theory]
    [InlineData("My Project", "my-project")]
    [InlineData("Hello World 123", "hello-world-123")]
    [InlineData("  Leading Trailing  ", "leading-trailing")]
    [InlineData("Special!@#$%Characters", "special-characters")]
    [InlineData("Multiple   Spaces", "multiple-spaces")]
    [InlineData("Already-Slug", "already-slug")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("dots.and.dashes-mixed", "dots-and-dashes-mixed")]
    public void Generate_ValidNames_ReturnsExpectedSlug(string name, string expected)
    {
        SlugGenerator.Generate(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_EmptyOrNull_ReturnsEmptyString(string name)
    {
        SlugGenerator.Generate(name).ShouldBe(string.Empty);
    }

    [Fact]
    public void Generate_SingleWord_ReturnsLowercase()
    {
        SlugGenerator.Generate("Production").ShouldBe("production");
    }
}
