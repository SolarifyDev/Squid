using System.Linq;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>Which version a push gets: replace only for a named version, a counter that moves past taken versions, and the version in the file name.</summary>
[Collection("BuiltInStaging")]
public class BuiltInPushVersionSelectionTests
{
    private static readonly byte[] HandPushed = TestArchives.Zip(("index.html", "hand pushed"));
    private static readonly byte[] Fresh = TestArchives.Zip(("index.html", "fresh"));
    private static readonly byte[] Other = TestArchives.Zip(("index.html", "other"));

    private readonly PushHarness _h = new();

    private static string Sha(byte[] bytes) => TestArchives.Sha256Of(bytes);

    private void SeedVersions(params string[] versions)
    {
        foreach (var version in versions)
            _h.Packages.Seed(_h.Feed.Id, "Acme.Web", version, Sha(HandPushed));
    }

    // === replace applies only to a version the caller named ===

    [Fact]
    public async Task Push_ReplaceWithoutVersion_NeverOverwritesATemplateAssignedVersion()
    {
        _h.Rules = new FakeRules("1.0.1");
        SeedVersions("1.0.1", "1.0.5");

        await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web", replace: true), CancellationToken.None));

        _h.Packages.Replaces.ShouldBe(0);
        _h.Packages.Rows.Single(r => r.Version == "1.0.1").Sha256.ShouldBe(Sha(HandPushed));
    }

    [Fact]
    public async Task Push_ReplaceWithoutVersion_UnderACounterRule_TakesTheNextFreeVersion_AndLeavesTheHandPushedOnesAlone()
    {
        SeedVersions("1.0.1", "1.0.5");

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web", replace: true), CancellationToken.None);

        result.Replaced.ShouldBeFalse();
        result.Package.Version.ShouldBe("1.0.2");
        _h.Packages.Replaces.ShouldBe(0);
        _h.Packages.Rows.Where(r => r.Version is "1.0.1" or "1.0.5").ShouldAllBe(r => r.Sha256 == Sha(HandPushed));
    }

    [Fact]
    public async Task Push_ReplaceWithoutVersion_OfTheLatestContent_IsDeduplicatedLikeAnyPlainPush()
    {
        await _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web"), CancellationToken.None);

        var again = await _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web", replace: true), CancellationToken.None);

        again.Deduplicated.ShouldBeTrue();
        _h.Packages.Rows.Count.ShouldBe(1);
    }

    // === a counter moves past versions the package already has ===

    [Fact]
    public async Task Push_CounterLandsOnExistingVersions_SkipsThemWithoutFailing()
    {
        SeedVersions("1.0.1", "1.0.2", "1.0.3");

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web"), CancellationToken.None);

        result.Package.Version.ShouldBe("1.0.4");
        _h.Packages.Rows.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Push_CounterOnlyTakesWhatItNeeds_SoTheNextPushContinuesAfterIt()
    {
        SeedVersions("1.0.1");

        var first = await _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web"), CancellationToken.None);
        var second = await _h.Service().PushAsync(PushHarness.Push(Other, packageId: "Acme.Web"), CancellationToken.None);

        first.Package.Version.ShouldBe("1.0.2");
        second.Package.Version.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task Push_CounterThatKeepsLandingOnExistingVersions_GivesUpAfterTheBound_WithAClearMessage()
    {
        SeedVersions(Enumerable.Range(1, BuiltInPackagePushService.MaxVersionAttempts).Select(n => $"1.0.{n}").ToArray());

        var ex = await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web"), CancellationToken.None));

        ex.Message.ShouldContain("version rule");
        ex.Message.ShouldContain($"{BuiltInPackagePushService.MaxVersionAttempts} versions in a row");
        _h.Packages.Rows.Count.ShouldBe(BuiltInPackagePushService.MaxVersionAttempts);
        _h.Counter.Calls.Count.ShouldBe(BuiltInPackagePushService.MaxVersionAttempts);
    }

    [Fact]
    public async Task Push_TemplateWithoutACounter_CannotMoveOn_AndKeepsFailingWithTheExistingVersionException()
    {
        _h.Rules = new FakeRules("2.0.0");
        SeedVersions("2.0.0");

        var ex = await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web"), CancellationToken.None));

        ex.Version.ShouldBe("2.0.0");
        ex.Message.ShouldNotContain("version rule");
    }

    [Fact]
    public async Task Push_VersionTakenByAnotherPushBetweenTheCheckAndTheInsert_MovesOnToTheNextNumber()
    {
        _h.Packages.BeforeInsert = row =>
        {
            _h.Packages.BeforeInsert = null;
            _h.Packages.Seed(_h.Feed.Id, "Acme.Web", row.Version, Sha(HandPushed));
        };

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web"), CancellationToken.None);

        result.Package.Version.ShouldBe("1.0.2");
        _h.Packages.Rows.Select(r => r.Version).OrderBy(v => v).ToArray().ShouldBe(["1.0.1", "1.0.2"]);
    }

    [Fact]
    public async Task Push_ExplicitVersionTakenBetweenTheCheckAndTheInsert_StillFails()
    {
        _h.Packages.BeforeInsert = row => _h.Packages.Seed(_h.Feed.Id, "Acme.Web", row.Version, Sha(HandPushed));

        await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => _h.Service().PushAsync(PushHarness.Push(Fresh, packageId: "Acme.Web", version: "3.0.0"), CancellationToken.None));

        _h.Packages.Inserts.ShouldBe(1);
    }

    // === the version in the file name ===

    [Fact]
    public async Task Push_NoVersionAndTheDefaultRule_UsesTheVersionInTheFileName()
    {
        _h.Rules = new FakeRules(isDefault: true);

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "Contoso.Api.2.5.0.zip"), CancellationToken.None);

        result.Package.PackageId.ShouldBe("Contoso.Api");
        result.Package.Version.ShouldBe("2.5.0");
        _h.Counter.Calls.ShouldBeEmpty("the counter is not touched when the file name gave the version");
    }

    [Fact]
    public async Task Push_NoVersionAndTheDefaultRule_UsesAPreReleaseVersionOfTheFileName()
    {
        _h.Rules = new FakeRules(isDefault: true);

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "Contoso.Api.2.5.0-beta.1.zip"), CancellationToken.None);

        result.Package.Version.ShouldBe("2.5.0-beta.1");
    }

    [Fact]
    public async Task Push_NoVersionButAnExplicitRule_TheRuleWins()
    {
        _h.Rules = new FakeRules("7.{counter}", isDefault: false);

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "Contoso.Api.2.5.0.zip"), CancellationToken.None);

        result.Package.Version.ShouldBe("7.1");
    }

    [Fact]
    public async Task Push_ExplicitVersion_WinsOverTheFileName()
    {
        _h.Rules = new FakeRules(isDefault: true);

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "Contoso.Api.2.5.0.zip", version: "9.9.9"), CancellationToken.None);

        result.Package.Version.ShouldBe("9.9.9");
    }

    [Fact]
    public async Task Push_NoVersionInTheFileName_FallsBackToTheTemplate()
    {
        _h.Rules = new FakeRules(isDefault: true);

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "upload.zip", packageId: "Contoso.Api"), CancellationToken.None);

        result.Package.Version.ShouldBe("1.0.1");
    }

    [Fact]
    public async Task Push_FileNameOfAnotherPackage_IsNotTrusted_ForTheVersion()
    {
        _h.Rules = new FakeRules(isDefault: true);

        var result = await _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "build.20261126.zip", packageId: "Contoso.Api"), CancellationToken.None);

        result.Package.Version.ShouldBe("1.0.1");
    }

    [Fact]
    public async Task Push_FileNameVersionThatExists_FailsLikeAnyVersionWithoutACounter()
    {
        _h.Rules = new FakeRules(isDefault: true);

        await _h.Service().PushAsync(PushHarness.Push(HandPushed, fileName: "Contoso.Api.2.5.0.zip"), CancellationToken.None);

        var ex = await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => _h.Service().PushAsync(PushHarness.Push(Fresh, fileName: "Contoso.Api.2.5.0.zip"), CancellationToken.None));

        ex.Version.ShouldBe("2.5.0");
    }
}
