using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Core.Services.Packages.Versioning.Tokens;

namespace Squid.UnitTests.Services.Packages.Versioning;

public class SourceVersionTokenTests
{
    private const string PackageJson = """{ "name": "app", "version": "4.5.6" }""";
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><VersionPrefix>7.0.0</VersionPrefix><Version>2.0.0-rc.1</Version></PropertyGroup></Project>";
    private const string Pyproject = "[project]\nname = \"app\"\nversion = \"3.2.1\"\n[tool.poetry]\nversion = \"9.9.9\"\n";

    private static Task<string> ResolveAsync(Dictionary<string, string> files, string argument = null)
    {
        return new SourceVersionToken().ResolveAsync(VersioningTestContexts.Context(files: files), argument, CancellationToken.None);
    }

    [Fact]
    public async Task PackageJson_WinsOverTheOtherManifests()
    {
        var files = new Dictionary<string, string> { ["package.json"] = PackageJson, ["App.csproj"] = Csproj, ["pyproject.toml"] = Pyproject };

        (await ResolveAsync(files)).ShouldBe("4.5.6");
    }

    [Fact]
    public async Task Csproj_IsNextWhenPackageJsonDeclaresNoVersion()
    {
        var files = new Dictionary<string, string> { ["package.json"] = """{ "name": "app" }""", ["App.csproj"] = Csproj, ["pyproject.toml"] = Pyproject };

        (await ResolveAsync(files)).ShouldBe("2.0.0-rc.1");
    }

    [Fact]
    public async Task Pyproject_IsLast_AndProjectTableBeatsPoetry()
    {
        (await ResolveAsync(new Dictionary<string, string> { ["pyproject.toml"] = Pyproject })).ShouldBe("3.2.1");
    }

    [Fact]
    public async Task Pyproject_FallsBackToPoetryTable()
    {
        var files = new Dictionary<string, string> { ["pyproject.toml"] = "[project]\nname = \"x\"\ndynamic = [\"version\"]\n\n[tool.poetry]\nname = 'x'\nversion = '0.4.2'  # note\n" };

        (await ResolveAsync(files)).ShouldBe("0.4.2");
    }

    [Fact]
    public async Task Csproj_OnlyAtTheRoot_AndTheFirstByNameIsUsed()
    {
        var files = new Dictionary<string, string> { ["src/Deep.csproj"] = Csproj, ["B.csproj"] = "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>", ["A.csproj"] = "<Project><PropertyGroup><Version>1.1.0</Version></PropertyGroup></Project>" };

        (await ResolveAsync(files)).ShouldBe("1.1.0");
    }

    [Fact]
    public async Task Csproj_IgnoresTheVersionOfAPackageReference()
    {
        var files = new Dictionary<string, string> { ["App.csproj"] = "<Project><ItemGroup><PackageReference Include=\"X\"><Version>9.9.9</Version></PackageReference></ItemGroup><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>" };

        (await ResolveAsync(files)).ShouldBe("1.2.3");
    }

    [Fact]
    public async Task ExplicitArgument_NamesOneManifest_EvenWhenAnEarlierOneHasAVersion()
    {
        var files = new Dictionary<string, string> { ["package.json"] = PackageJson, ["pyproject.toml"] = Pyproject, ["src/App.csproj"] = Csproj };

        (await ResolveAsync(files, "pyproject.toml")).ShouldBe("3.2.1");
        (await ResolveAsync(files, "src/App.csproj")).ShouldBe("2.0.0-rc.1");
    }

    [Fact]
    public async Task ExplicitArgument_MissingFile_SaysWhichOne_AndDoesNotFallBack()
    {
        var files = new Dictionary<string, string> { ["package.json"] = PackageJson };

        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => ResolveAsync(files, "pyproject.toml"));

        ex.Message.ShouldBe("Version token '{source.version}' has no value: 'pyproject.toml' is missing from the package or declares no version");
    }

    [Theory]
    [InlineData("setup.py", "supported manifests are package.json, *.csproj and pyproject.toml")]
    [InlineData("mypackage.json", "supported manifests are package.json, *.csproj and pyproject.toml")]
    [InlineData("../package.json", "the path must be relative and inside the package")]
    [InlineData("/etc/package.json", "the path must be relative and inside the package")]
    public async Task ExplicitArgument_NotASupportedManifest_IsRejected(string argument, string detail)
    {
        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => ResolveAsync(new Dictionary<string, string>(), argument));

        ex.Message.ShouldBe($"Version token '{{source.version}}' cannot take the argument '{argument}': {detail}");
    }

    [Fact]
    public async Task NoManifestDeclaresAVersion_ListsWhatWasLookedAt()
    {
        var files = new Dictionary<string, string> { ["package.json"] = """{ "name": "app" }""", ["App.csproj"] = "<Project />" };

        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => ResolveAsync(files));

        ex.Message.ShouldBe("Version token '{source.version}' has no value: none of package.json, App.csproj, pyproject.toml in the package declares a version");
    }

    [Fact]
    public async Task UnreadableManifest_SaysWhichAndWhy()
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => ResolveAsync(new Dictionary<string, string> { ["package.json"] = "{ not json" }));

        ex.Message.ShouldStartWith("Version token '{source.version}' has no value: 'package.json' cannot be read: it is not valid JSON");
    }

    [Fact]
    public async Task NoPackageContent_SaysSo()
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => new SourceVersionToken().ResolveAsync(VersioningTestContexts.Context(), null, CancellationToken.None));

        ex.Message.ShouldBe("Version token '{source.version}' has no value: the package content is not available, so no manifest can be read from it");
    }

    [Fact]
    public void Csproj_WithADoctype_IsRefusedNotExpanded()
    {
        var bomb = "<!DOCTYPE p [<!ENTITY a \"aaaa\">]><Project><PropertyGroup><Version>&a;</Version></PropertyGroup></Project>";

        Should.Throw<FormatException>(() => ManifestVersionParser.FromCsproj(bomb));
    }

    [Fact]
    public void PackageJson_VersionThatIsNotAString_IsNoVersion()
    {
        ManifestVersionParser.FromPackageJson("""{ "version": 3 }""").ShouldBeNull();
    }

    [Fact]
    public void Pyproject_VersionInAnArrayOfTablesIsNotTheProjectsVersion()
    {
        ManifestVersionParser.FromPyproject("[project]\nname = \"x\"\n[[tool.other]]\nversion = \"5.0.0\"\n").ShouldBeNull();
    }
}
