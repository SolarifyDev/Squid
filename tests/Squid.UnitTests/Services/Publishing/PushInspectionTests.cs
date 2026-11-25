using System.Linq;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Inspectors;
using Squid.Message.Models.Publishing;
using Squid.UnitTests.Services.Packages.BuiltIn;

namespace Squid.UnitTests.Services.Publishing;

/// <summary>The inspection step of the push pipeline, over the in-memory push harness.</summary>
[Collection("BuiltInStaging")]
public class PushInspectionTests
{
    private readonly PushHarness _h = new();

    [Fact]
    public async Task Push_StoresTheFacts_OnTheRow_AndReturnsThemInTheResponse()
    {
        var bytes = TestArchives.Zip(("site/index.html", "<html/>"), ("site/Dockerfile", "FROM x"));

        var result = await _h.Service().PushAsync(PushHarness.Push(bytes), CancellationToken.None);

        var stored = PackageFactsJson.Deserialize(_h.Packages.Rows.Single().Facts);

        stored.Get(PackageFactKeys.ArchiveRoot).ShouldBe("site");
        stored.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
        stored.Get(PackageFactKeys.FilesCount).ShouldBe("2");
        stored.Files.ShouldBeEmpty("the file list is not persisted");
        _h.Packages.Rows.Single().Facts.ShouldNotContain("index.html");
        result.Package.Facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
    }

    [Fact]
    public async Task Push_WithAnInspectorThatThrows_StillSucceeds_AndRecordsTheFailure()
    {
        _h.Inspectors = [new ThrowingInspector(), new FileMarkerInspector()];

        var result = await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("Dockerfile", "FROM x"))), CancellationToken.None);

        _h.Packages.Rows.Count.ShouldBe(1);
        result.Package.Facts.Get(PackageFactKeys.InspectorFailedPrefix + nameof(ThrowingInspector)).ShouldBe("boom");
        result.Package.Facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
    }

    [Fact]
    public async Task Push_ReplacingAVersion_ReplacesItsFacts()
    {
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("Dockerfile", "FROM x")), version: "1.0.0"), CancellationToken.None);
        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("index.html", "<html/>")), version: "1.0.0", replace: true), CancellationToken.None);

        var stored = PackageFactsJson.Deserialize(_h.Packages.Rows.Single().Facts);

        stored.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
        stored.Values.ContainsKey(PackageFactKeys.MarkerDockerfile).ShouldBeFalse();
    }

    [Fact]
    public async Task Push_OfIdenticalBytes_DoesNotInspectAgain()
    {
        var bytes = TestArchives.Zip(("index.html", "<html/>"));
        var spy = new CountingInspector();

        _h.Inspectors = [spy];

        await _h.Service().PushAsync(PushHarness.Push(bytes), CancellationToken.None);
        await _h.Service().PushAsync(PushHarness.Push(bytes), CancellationToken.None);

        spy.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task Push_ThatIsRefusedBeforeInspection_NeverInspects()
    {
        var spy = new CountingInspector();

        _h.Inspectors = [spy];

        await Should.ThrowAsync<Exception>(() => _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("../evil.txt", "x"))), CancellationToken.None));

        spy.Runs.ShouldBe(0, "inspection runs only after the archive is verified safe");
    }

    [Fact]
    public void Facts_RoundTripThroughJson_AndAnAbsentColumnReadsAsNotInspected()
    {
        var facts = new PackageFacts { Files = ["a/b.txt"], Values = { ["k"] = "v" } };

        var back = PackageFactsJson.Deserialize(PackageFactsJson.Serialize(facts));

        back.Files.ShouldBeEmpty();
        back.Values["k"].ShouldBe("v");
        PackageFactsJson.Deserialize(null).ShouldBeNull();
        PackageFactsJson.Serialize(null).ShouldBeNull();
        BuiltInPackageMapper.ToDto(new Squid.Core.Persistence.Entities.Deployments.BuiltInPackage { Facts = null }, withFacts: true).Facts.ShouldBeNull();
    }

    [Fact]
    public void ListDtos_LeaveTheFactsOut()
    {
        var row = new Squid.Core.Persistence.Entities.Deployments.BuiltInPackage { Facts = PackageFactsJson.Serialize(new PackageFacts { Values = { ["k"] = "v" } }) };

        BuiltInPackageMapper.ToDto(row).Facts.ShouldBeNull();
        BuiltInPackageMapper.ToDto(row, withFacts: true).Facts.Values["k"].ShouldBe("v");
    }

    [Fact]
    public void Facts_StoredWithAFileList_StillRead_AndTheListIsDropped()
    {
        var old = PackageFactsJson.Deserialize("""{"Files":["a/b.txt","c.txt"],"Values":{"marker.dockerfile":"true"}}""");

        old.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
        old.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_OfA5000FileArchive_StoresSmallFacts()
    {
        var entries = Enumerable.Range(0, 5000).Select(i => ($"assets/some/deep/folder/file-number-{i}.txt", "x")).ToArray();

        await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(entries)), CancellationToken.None);

        var json = _h.Packages.Rows.Single().Facts;

        json.Length.ShouldBeLessThan(1024);
        PackageFactsJson.Deserialize(json).Get(PackageFactKeys.FilesCount).ShouldBe("5000");
    }

    [Fact]
    public void Recommendations_FromAStoredRow_RankFromValuesAlone()
    {
        var facts = PackageFactsJson.Deserialize(PackageFactsJson.Serialize(new PackageFacts { Values = { [PackageFactKeys.MarkerDockerfile] = "true" } }));
        var registry = new DeployRecipeRegistry([new ValueRecipe("docker", PackageFactKeys.MarkerDockerfile), new ValueRecipe("static", PackageFactKeys.MarkerIndexHtml)]);

        registry.Select(facts).Candidates.Select(c => c.Kind).ShouldBe(["docker"]);
    }

    private sealed class ValueRecipe(string kind, string key) : IDeployRecipe
    {
        public string Kind { get; } = kind;

        public RecipeMatch Match(PackageFacts facts) => new() { Score = facts.Is(key) ? 80 : 0, Reasons = [key] };

        public Task<DeploymentPlan> RenderAsync(RecipeRenderContext context, CancellationToken ct) => Task.FromResult(new DeploymentPlan());
    }

    private sealed class CountingInspector : IPackageInspector
    {
        public int Runs { get; private set; }

        public Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
        {
            Runs++;

            return Task.CompletedTask;
        }
    }
}
