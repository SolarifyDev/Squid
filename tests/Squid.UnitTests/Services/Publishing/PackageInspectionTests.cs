using System.Linq;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;
using Squid.UnitTests.Services.Packages.BuiltIn;

namespace Squid.UnitTests.Services.Publishing;

/// <summary>The inspectors over real archives: effective root, markers, package.json, squid.yaml, and the bounds on reading.</summary>
public class PackageInspectionTests
{
    private const string Html = "<html/>";

    // === effective root ===

    [Fact]
    public async Task Root_SingleWrapperFolder_IsDetected_AndMarkersAreReadRelativeToIt()
    {
        var facts = await Inspect.ZipAsync(("my-site/index.html", Html), ("my-site/css/app.css", "a{}"), ("my-site/Dockerfile", "FROM x"));

        facts.Is(PackageFactKeys.ArchiveWrapped).ShouldBeTrue();
        facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe("my-site");
        facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
        facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
        facts.RelativeFiles().OrderBy(f => f, StringComparer.Ordinal).ShouldBe(["Dockerfile", "css/app.css", "index.html"]);
        facts.Files.ShouldContain("my-site/index.html");
    }

    [Fact]
    public async Task Root_DotSlashPrefixedTar_IsStrippedFromEveryPath()
    {
        var facts = await Inspect.TarGzAsync(("./index.html", Html), ("./app/run.sh", "echo"));

        facts.Files.OrderBy(f => f).ShouldBe(["app/run.sh", "index.html"]);
        facts.Is(PackageFactKeys.ArchiveWrapped).ShouldBeFalse();
        facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe(string.Empty);
        facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
    }

    [Fact]
    public async Task Root_DotSlashWrapperFolder_IsDetectedInATar()
    {
        var facts = await Inspect.TarGzAsync(("./site/index.html", Html), ("./site/a.js", "1"));

        facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe("site");
        facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
    }

    [Fact]
    public async Task Root_FlatArchive_HasNoWrapper()
    {
        var facts = await Inspect.ZipAsync(("index.html", Html), ("a/b.js", "1"));

        facts.Is(PackageFactKeys.ArchiveWrapped).ShouldBeFalse();
        facts.RootPrefix().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Root_TwoTopLevelFolders_HasNoWrapper_AndMarkersAreNotFoundInsideThem()
    {
        var facts = await Inspect.ZipAsync(("a/index.html", Html), ("b/Dockerfile", "FROM x"));

        facts.Is(PackageFactKeys.ArchiveWrapped).ShouldBeFalse();
        facts.Values.ContainsKey(PackageFactKeys.MarkerIndexHtml).ShouldBeFalse();
        facts.Values.ContainsKey(PackageFactKeys.MarkerDockerfile).ShouldBeFalse();
    }

    [Fact]
    public async Task Root_MacResourceForkFolderBesideTheWrapper_IsIgnored()
    {
        var facts = await Inspect.ZipAsync(("site/index.html", Html), ("__MACOSX/site/._index.html", "x"));

        facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe("site");
        facts.Files.ShouldBe(["site/index.html"]);
    }

    [Fact]
    public async Task Root_EmptyArchive_HasNoFactsAndNoFailure()
    {
        var facts = await Inspect.ZipAsync();

        facts.Files.ShouldBeEmpty();
        facts.Values.Keys.Any(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeFalse();
    }

    // === file markers ===

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("compose.yml")]
    [InlineData("docker-compose.yaml")]
    [InlineData("docker-compose.yml")]
    public async Task Markers_ComposeFile_IsDetected(string name)
    {
        (await Inspect.ZipAsync((name, "services: {}"))).Is(PackageFactKeys.MarkerCompose).ShouldBeTrue();
    }

    [Fact]
    public async Task Markers_DockerfileAndSquidYaml_AreDetectedAtTheRoot()
    {
        var facts = await Inspect.ZipAsync(("Dockerfile", "FROM x"), ("squid.yaml", "recipe: docker"));

        facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
        facts.Is(PackageFactKeys.MarkerSquidYaml).ShouldBeTrue();
        facts.Values.ContainsKey(PackageFactKeys.MarkerCompose).ShouldBeFalse();
    }

    [Theory]
    [InlineData("index.html", ".")]
    [InlineData("dist/index.html", "dist")]
    [InlineData("build/index.html", "build")]
    [InlineData("out/index.html", "out")]
    [InlineData("public/index.html", "public")]
    public async Task Markers_IndexHtml_IsFoundAtTheRootOrInABuildFolder(string path, string expectedDir)
    {
        var facts = await Inspect.ZipAsync((path, Html), ("readme.md", "x"));

        facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
        facts.Get(PackageFactKeys.MarkerIndexHtmlDir).ShouldBe(expectedDir);
    }

    [Fact]
    public async Task Markers_IndexHtmlInBothRootAndDist_ReportsTheRoot()
    {
        var facts = await Inspect.ZipAsync(("index.html", Html), ("dist/index.html", Html));

        facts.Get(PackageFactKeys.MarkerIndexHtmlDir).ShouldBe(".");
    }

    [Fact]
    public async Task Markers_IndexHtmlInAnUnlistedFolder_IsNotAMarker()
    {
        var facts = await Inspect.ZipAsync(("docs/index.html", Html), ("readme.md", "x"));

        facts.Values.ContainsKey(PackageFactKeys.MarkerIndexHtml).ShouldBeFalse();
    }

    [Fact]
    public async Task Markers_FileNamesAreCaseSensitive()
    {
        var facts = await Inspect.ZipAsync(("dockerfile", "FROM x"), ("readme.md", "x"));

        facts.Values.ContainsKey(PackageFactKeys.MarkerDockerfile).ShouldBeFalse();
    }

    // === node ===

    [Fact]
    public async Task Node_FullPackageJson_RecordsScriptsAndEngines()
    {
        var facts = await Inspect.ZipAsync(("package.json", """{"name":"x","scripts":{"build":"vite build","start":"node server.js"},"engines":{"node":">=20"}}"""));

        facts.Is(PackageFactKeys.NodePackageJson).ShouldBeTrue();
        facts.Is(PackageFactKeys.NodeBuildScript).ShouldBeTrue();
        facts.Is(PackageFactKeys.NodeStartScript).ShouldBeTrue();
        facts.Get(PackageFactKeys.NodeEngines).ShouldBe(">=20");
    }

    [Fact]
    public async Task Node_PackageJsonWithoutScriptsOrEngines_RecordsOnlyItsPresence()
    {
        var facts = await Inspect.ZipAsync(("package.json", """{"name":"x"}"""));

        facts.Is(PackageFactKeys.NodePackageJson).ShouldBeTrue();
        facts.Values.Keys.Where(k => k.StartsWith("node.")).ShouldBe([PackageFactKeys.NodePackageJson]);
    }

    [Fact]
    public async Task Node_OnlyTheScriptsThatExist_AreRecorded()
    {
        var facts = await Inspect.ZipAsync(("package.json", """{"scripts":{"start":"node a.js","test":"x"}}"""));

        facts.Is(PackageFactKeys.NodeStartScript).ShouldBeTrue();
        facts.Values.ContainsKey(PackageFactKeys.NodeBuildScript).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{"scripts":{"build":""")]
    public async Task Node_MalformedPackageJson_IsAFact_NotAFailure(string content)
    {
        var facts = await Inspect.ZipAsync(("package.json", content));

        facts.Is(PackageFactKeys.NodePackageJson).ShouldBeTrue();
        facts.Get(PackageFactKeys.NodeError).ShouldNotBeNullOrEmpty();
        facts.Values.Keys.Any(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeFalse();
    }

    [Fact]
    public async Task Node_PackageJsonThatIsNotAnObject_OrHasOddTypes_DoesNotThrow()
    {
        (await Inspect.ZipAsync(("package.json", "[1,2]"))).Values.Keys.Any(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeFalse();

        var facts = await Inspect.ZipAsync(("package.json", """{"scripts":"nope","engines":{"node":18}}"""));

        facts.Values.Keys.Any(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeFalse();
        facts.Values.ContainsKey(PackageFactKeys.NodeEngines).ShouldBeFalse();
    }

    [Fact]
    public async Task Node_PackageJsonWithABom_IsParsed()
    {
        var facts = await Inspect.ZipAsync(("package.json", "﻿{\"scripts\":{\"build\":\"x\"}}"));

        facts.Is(PackageFactKeys.NodeBuildScript).ShouldBeTrue();
    }

    [Fact]
    public async Task Node_PackageJsonInANestedFolder_IsNotThePackages()
    {
        var facts = await Inspect.ZipAsync(("web/package.json", "{}"), ("index.html", Html));

        facts.Values.ContainsKey(PackageFactKeys.NodePackageJson).ShouldBeFalse();
    }

    // === squid.yaml ===

    [Fact]
    public async Task SquidYaml_Valid_RecordsEveryField()
    {
        var facts = await Inspect.ZipAsync(("squid.yaml", "recipe: Docker\nrun:\n  port: 8080\n  command: node server.js\nhealth:\n  path: /healthz\n"));

        facts.Get(PackageFactKeys.SquidRecipe).ShouldBe("Docker");
        facts.Get(PackageFactKeys.SquidRunPort).ShouldBe("8080");
        facts.Get(PackageFactKeys.SquidRunCommand).ShouldBe("node server.js");
        facts.Get(PackageFactKeys.SquidHealthPath).ShouldBe("/healthz");
        facts.Values.ContainsKey(PackageFactKeys.SquidError).ShouldBeFalse();
    }

    [Fact]
    public async Task SquidYaml_Partial_RecordsOnlyWhatIsThere()
    {
        var facts = await Inspect.ZipAsync(("squid.yaml", "run:\n  port: 3000\n"));

        facts.Get(PackageFactKeys.SquidRunPort).ShouldBe("3000");
        facts.Values.ContainsKey(PackageFactKeys.SquidRecipe).ShouldBeFalse();
        facts.Values.ContainsKey(PackageFactKeys.SquidRunCommand).ShouldBeFalse();
        facts.Values.ContainsKey(PackageFactKeys.SquidHealthPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("recipe: [unclosed")]
    [InlineData("a: b: c: d")]
    [InlineData("- just\n- a list")]
    [InlineData("a: 'unterminated")]
    public async Task SquidYaml_Invalid_IsAnErrorFact_NotAFailure(string content)
    {
        var facts = await Inspect.ZipAsync(("squid.yaml", content), ("index.html", Html));

        facts.Get(PackageFactKeys.SquidError).ShouldNotBeNullOrEmpty();
        facts.Values.Keys.Any(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeFalse();
        facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue("a bad squid.yaml must not stop the other inspectors");
    }

    [Fact]
    public async Task SquidYaml_Empty_RecordsNothing()
    {
        var facts = await Inspect.ZipAsync(("squid.yaml", ""));

        facts.Is(PackageFactKeys.MarkerSquidYaml).ShouldBeTrue();
        facts.Values.Keys.Any(k => k.StartsWith("squid.")).ShouldBeFalse();
    }

    // === bounds ===

    [Fact]
    public async Task OversizedFile_IsSkippedWithAFact_AndNeverRead()
    {
        var huge = new string(' ', PackageArchiveTextReader.MaxTextBytes + 1) + """{"scripts":{"build":"x"}}""";

        var facts = await Inspect.ZipAsync(("site/package.json", huge), ("site/index.html", Html));

        facts.Is(PackageFactKeys.NodePackageJson).ShouldBeTrue("its presence is known from the listing");
        facts.Values.ContainsKey(PackageFactKeys.NodeBuildScript).ShouldBeFalse("an oversized file is not read");
        facts.Get(PackageFactKeys.FilesOversized).ShouldBe("site/package.json");
    }

    [Fact]
    public async Task FileExactlyAtTheCap_IsRead()
    {
        var json = """{"scripts":{"build":"x"}}""";
        var atCap = json + new string(' ', PackageArchiveTextReader.MaxTextBytes - json.Length);

        (await Inspect.ZipAsync(("package.json", atCap))).Is(PackageFactKeys.NodeBuildScript).ShouldBeTrue();
    }

    [Fact]
    public async Task OversizedFileInATar_IsSkippedWithAFact()
    {
        var huge = new string(' ', PackageArchiveTextReader.MaxTextBytes + 1);

        var facts = await Inspect.TarGzAsync(("squid.yaml", huge));

        facts.Get(PackageFactKeys.FilesOversized).ShouldBe("squid.yaml");
        facts.Values.ContainsKey(PackageFactKeys.SquidError).ShouldBeFalse();
    }

    [Fact]
    public async Task FileListing_IsCapped_AndSaysSo()
    {
        var entries = Enumerable.Range(0, PackageArchiveTextReader.MaxListedFiles + 5).Select(i => ($"f{i}.txt", "x")).ToArray();

        var zip = await Inspect.ZipAsync(entries);
        var tar = await Inspect.TarGzAsync(entries);

        zip.Files.Count.ShouldBe(PackageArchiveTextReader.MaxListedFiles);
        tar.Files.Count.ShouldBe(PackageArchiveTextReader.MaxListedFiles);
        zip.Is(PackageFactKeys.FilesTruncated).ShouldBeTrue();
        tar.Is(PackageFactKeys.FilesTruncated).ShouldBeTrue();
    }

    [Fact]
    public async Task FileListing_AtTheCap_IsNotTruncated()
    {
        var entries = Enumerable.Range(0, PackageArchiveTextReader.MaxListedFiles).Select(i => ($"f{i}.txt", "x")).ToArray();

        (await Inspect.ZipAsync(entries)).Values.ContainsKey(PackageFactKeys.FilesTruncated).ShouldBeFalse();
    }

    // === failures ===

    [Fact]
    public async Task ThrowingInspector_BecomesAFact_AndTheOthersStillRun()
    {
        var facts = await Inspect.RunAsync(TestArchives.Zip(("Dockerfile", "FROM x")), BuiltInPackageFormat.Zip, new ThrowingInspector(), new Squid.Core.Services.Publishing.Inspectors.FileMarkerInspector());

        facts.Get(PackageFactKeys.InspectorFailedPrefix + nameof(ThrowingInspector)).ShouldBe("boom");
        facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
    }

    [Fact]
    public async Task CorruptArchive_YieldsAFailureFact_NotAnException()
    {
        var facts = await Inspect.RunAsync([1, 2, 3, 4, 5], BuiltInPackageFormat.Zip);

        facts.Files.ShouldBeEmpty();
        facts.Values.Keys.ShouldContain(PackageFactKeys.InspectorFailedPrefix + "Listing");
    }

    // === pinned keys ===

    [Fact]
    public void FactKeys_ArePinned()
    {
        PackageFactKeys.ArchiveWrapped.ShouldBe("archive.wrapped");
        PackageFactKeys.ArchiveRoot.ShouldBe("archive.root");
        PackageFactKeys.FilesCount.ShouldBe("files.count");
        PackageFactKeys.FilesTruncated.ShouldBe("files.truncated");
        PackageFactKeys.FilesOversized.ShouldBe("files.oversized");
        PackageFactKeys.MarkerDockerfile.ShouldBe("marker.dockerfile");
        PackageFactKeys.MarkerCompose.ShouldBe("marker.compose");
        PackageFactKeys.MarkerIndexHtml.ShouldBe("marker.index-html");
        PackageFactKeys.MarkerIndexHtmlDir.ShouldBe("marker.index-html.dir");
        PackageFactKeys.MarkerSquidYaml.ShouldBe("marker.squid-yaml");
        PackageFactKeys.NodePackageJson.ShouldBe("node.package-json");
        PackageFactKeys.NodeBuildScript.ShouldBe("node.build-script");
        PackageFactKeys.NodeStartScript.ShouldBe("node.start-script");
        PackageFactKeys.NodeEngines.ShouldBe("node.engines");
        PackageFactKeys.NodeError.ShouldBe("node.error");
        PackageFactKeys.SquidRecipe.ShouldBe("squid.recipe");
        PackageFactKeys.SquidRunPort.ShouldBe("squid.run.port");
        PackageFactKeys.SquidRunCommand.ShouldBe("squid.run.command");
        PackageFactKeys.SquidHealthPath.ShouldBe("squid.health.path");
        PackageFactKeys.SquidError.ShouldBe("squid.error");
        PackageFactKeys.InspectorFailedPrefix.ShouldBe("inspector.failed.");
        PackageFactKeys.True.ShouldBe("true");
    }

    [Fact]
    public void FactKeys_AreAllDistinct()
    {
        var values = typeof(PackageFactKeys).GetFields().Select(f => (string)f.GetRawConstantValue()).ToList();

        values.Distinct().Count().ShouldBe(values.Count);
    }
}
