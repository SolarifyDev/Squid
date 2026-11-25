using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Packages.Versioning;
using Squid.Core.Services.Packages.Versioning.Exceptions;

namespace Squid.UnitTests.Services.Packages.Versioning;

public class PackageVersionAssignerTests
{
    private readonly FakeCounterStore _store = new();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));

    private PackageVersionAssigner Sut => VersioningTestContexts.AssignerWith(_store, _clock);

    [Fact]
    public async Task Counter_CountsUpPerPackage()
    {
        var context = VersioningTestContexts.Context();

        (await Sut.AssignAsync("1.0.{counter}", context, CancellationToken.None)).ShouldBe("1.0.1");
        (await Sut.AssignAsync("1.0.{counter}", context, CancellationToken.None)).ShouldBe("1.0.2");
        (await Sut.AssignAsync("1.0.{counter}", context with { PackageId = "Other" }, CancellationToken.None)).ShouldBe("1.0.1");
    }

    [Fact]
    public async Task DifferentTemplatePrefixes_HaveTheirOwnCounter()
    {
        var context = VersioningTestContexts.Context();

        (await Sut.AssignAsync("1.0.{counter}", context, CancellationToken.None)).ShouldBe("1.0.1");
        (await Sut.AssignAsync("2.0.{counter}", context, CancellationToken.None)).ShouldBe("2.0.1");
    }

    [Fact]
    public async Task DateAndDailyCounter_GiveFourPartVersionsThatRestartEachDay_AndSortAsVersions()
    {
        var context = VersioningTestContexts.Context();
        var first = await Sut.AssignAsync("{date:yyyy.MM.dd}.{counter:daily}", context, CancellationToken.None);
        var second = await Sut.AssignAsync("{date:yyyy.MM.dd}.{counter:daily}", context, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddDays(1);

        var nextDay = await Sut.AssignAsync("{date:yyyy.MM.dd}.{counter:daily}", context, CancellationToken.None);

        new[] { first, second, nextDay }.ShouldBe(["2026.11.25.1", "2026.11.25.2", "2026.11.26.1"]);
        PackageVersionFilter.SortByVersionDescending([first, nextDay, second]).ShouldBe([nextDay, second, first]);
    }

    [Fact]
    public async Task DateBeforePlainCounter_AlreadyRestartsEachDay()
    {
        var context = VersioningTestContexts.Context();

        (await Sut.AssignAsync("{date}.{counter}", context, CancellationToken.None)).ShouldBe("2026.11.25.1");

        _clock.UtcNow = _clock.UtcNow.AddDays(1);

        (await Sut.AssignAsync("{date}.{counter}", context, CancellationToken.None)).ShouldBe("2026.11.26.1");
    }

    [Fact]
    public async Task Tokens_AreMatchedIgnoringCase_AndLiteralsAroundThemSurvive()
    {
        var context = VersioningTestContexts.Context(input: "5");

        (await Sut.AssignAsync("v1.{INPUT}.{Counter}-rc", context, CancellationToken.None)).ShouldBe("v1.5.1-rc");
    }

    [Fact]
    public async Task EveryTokenResolves()
    {
        var files = new Dictionary<string, string> { ["package.json"] = """{ "version": "4.5.6" }""" };
        var context = VersioningTestContexts.Context("Acme.Web.1.2.3.zip", "9.9.9", "abcdef0123456789", files);

        (await Sut.AssignAsync("{file.version}", context, CancellationToken.None)).ShouldBe("1.2.3");
        (await Sut.AssignAsync("{source.version}", context, CancellationToken.None)).ShouldBe("4.5.6");
        (await Sut.AssignAsync("{input}", context, CancellationToken.None)).ShouldBe("9.9.9");
        (await Sut.AssignAsync("1.0.0-{git.sha:9}", context, CancellationToken.None)).ShouldBe("1.0.0-abcdef012");
    }

    [Fact]
    public async Task UnknownToken_NamesItAndTheKnownOnes_AndConsumesNothing()
    {
        var ex = await Should.ThrowAsync<UnknownVersionTokenException>(() => Sut.AssignAsync("1.0.{counter}.{build}", VersioningTestContexts.Context(), CancellationToken.None));

        ex.Message.ShouldBe("Version token '{build}' is not known. Known tokens: counter, date, file.version, git.sha, input, source.version");
        _store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingValue_NeverFallsBackToAnotherRule()
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => Sut.AssignAsync("{input}", VersioningTestContexts.Context(), CancellationToken.None));

        ex.Message.ShouldBe("Version token '{input}' has no value: no version was entered; type a version or choose another version rule");
        _store.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{counter}-{git.sha}")]
    [InlineData("1.0.{counter}-{git.sha}")]
    [InlineData("1.0.{counter}.{input}")]
    [InlineData("1.0.{counter}-{source.version}")]
    public async Task ATokenThatFailsAfterTheCounter_DoesNotConsumeANumber(string template)
    {
        await Should.ThrowAsync<PackageVersionTemplateException>(() => Sut.AssignAsync(template, VersioningTestContexts.Context(), CancellationToken.None));

        _store.Calls.ShouldBeEmpty();
        (await Sut.AssignAsync("1.0.{counter}", VersioningTestContexts.Context(), CancellationToken.None)).ShouldBe("1.0.1");
    }

    [Theory]
    [InlineData("{counter}", "1", "major.minor")]
    [InlineData("1.0.{counter}.{counter}.{counter}", "1.0.1.1.1", "major.minor")]
    public async Task ResultThatIsNotSortable_IsRejectedBeforeACounterIsConsumed(string template, string produced, string hint)
    {
        var ex = await Should.ThrowAsync<NonSortablePackageVersionException>(() => Sut.AssignAsync(template, VersioningTestContexts.Context(), CancellationToken.None));

        ex.Version.ShouldBe(produced);
        ex.Message.ShouldContain(hint);
        _store.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("v")]
    public async Task InputThatDoesNotSortAsAVersion_IsRejected(string input)
    {
        var ex = await Should.ThrowAsync<NonSortablePackageVersionException>(() => Sut.AssignAsync("{input}", VersioningTestContexts.Context(input: input), CancellationToken.None));

        ex.Message.ShouldBe($"Version template '{{input}}' produced '{input}', which is not a usable version: use the form major.minor[.patch[.revision]][-prerelease], for example 1.0.{{counter}}, so feed versions sort and channel version rules apply");
    }

    [Theory]
    [InlineData("1.0.0-../../x")]
    [InlineData("1.0.0 beta")]
    [InlineData("1.0.0-a/b")]
    public async Task InputWithCharactersAVersionNeverHas_IsRejected(string input)
    {
        var ex = await Should.ThrowAsync<NonSortablePackageVersionException>(() => Sut.AssignAsync("{input}", VersioningTestContexts.Context(input: input), CancellationToken.None));

        ex.Message.ShouldEndWith("it may only contain letters, digits, '.', '-' and '+'");
    }

    [Fact]
    public async Task Preview_NeverTouchesTheCounter_AndAssignmentStillStartsAtOne()
    {
        var context = VersioningTestContexts.Context();

        (await Sut.PreviewAsync("1.0.{counter}", context, CancellationToken.None)).ShouldBe("1.0.1");
        (await Sut.PreviewAsync("{date}.{counter:daily}", context, CancellationToken.None)).ShouldBe("2026.11.25.1");

        _store.Calls.ShouldBeEmpty();
        (await Sut.AssignAsync("1.0.{counter}", context, CancellationToken.None)).ShouldBe("1.0.1");
    }

    [Fact]
    public async Task Preview_WithTheSampleUpload_ChecksEveryToken_WithoutRealContent()
    {
        var sample = PackageVersionSampleContext.Create(7, "Acme.Web");

        (await Sut.PreviewAsync("{source.version}", sample, CancellationToken.None)).ShouldBe("1.2.3");
        (await Sut.PreviewAsync("{source.version:src/App.csproj}", sample, CancellationToken.None)).ShouldBe("1.2.3");
        (await Sut.PreviewAsync("{source.version:pyproject.toml}", sample, CancellationToken.None)).ShouldBe("1.2.3");
        (await Sut.PreviewAsync("{file.version}", sample, CancellationToken.None)).ShouldBe("1.2.3");
        (await Sut.PreviewAsync("{input}", sample, CancellationToken.None)).ShouldBe("1.2.3");
        (await Sut.PreviewAsync("1.0.0-{git.sha}", sample, CancellationToken.None)).ShouldBe("1.0.0-0123456");
        _store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preview_RejectsABadTemplate_LikeAssignDoes()
    {
        await Should.ThrowAsync<MalformedVersionTemplateException>(() => Sut.PreviewAsync("1.0.{counter", VersioningTestContexts.Context(), CancellationToken.None));
        await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => Sut.PreviewAsync("1.0.{counter:weekly}", VersioningTestContexts.Context(), CancellationToken.None));
        await Should.ThrowAsync<NonSortablePackageVersionException>(() => Sut.PreviewAsync("{counter}", VersioningTestContexts.Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ATemplateWithoutTokens_IsAFixedVersion()
    {
        (await Sut.AssignAsync("2.0.0", VersioningTestContexts.Context(), CancellationToken.None)).ShouldBe("2.0.0");
    }
}
