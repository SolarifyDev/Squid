using System.Linq;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Core.Services.Packages.Versioning.Tokens;

namespace Squid.UnitTests.Services.Packages.Versioning;

public class FileInputAndGitTokenTests
{
    [Theory]
    [InlineData("Acme.Web.1.2.3.zip", "1.2.3")]
    [InlineData("Acme.Web.1.2.3-beta.1.zip", "1.2.3-beta.1")]
    [InlineData("Acme.Web.1.2.3-beta.1.tar.gz", "1.2.3-beta.1")]
    [InlineData("Acme.1.0.0.tar.gz", "1.0.0")]
    [InlineData("Acme.1.0.0.TGZ", "1.0.0")]
    [InlineData("Acme.1.0.tar", "1.0")]
    [InlineData("Acme.Web.Api.2026.11.25.3.nupkg", "2026.11.25.3")]
    [InlineData("acme_app-x.4.5.6.nupkg", "4.5.6")]
    [InlineData("Acme.1.2.3+build.5.zip", "1.2.3+build.5")]
    [InlineData("incoming/builds/Acme.1.2.3.zip", "1.2.3")]
    public async Task FileVersion_ReadsTheVersionOfPackageIdDotVersionDotExtension(string fileName, string expected)
    {
        var value = await new FileVersionToken().ResolveAsync(VersioningTestContexts.Context(fileName), null, CancellationToken.None);

        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Acme.zip", "Version token '{file.version}' has no value: the file name 'Acme.zip' has no version; expected PackageId.Version.ext, for example Acme.Web.1.2.3.zip")]
    [InlineData("Acme.1.2.3.rar", "Version token '{file.version}' has no value: the file name 'Acme.1.2.3.rar' does not end in a supported extension (.tar.gz, .tgz, .nupkg, .zip, .tar)")]
    [InlineData(null, "Version token '{file.version}' has no value: the upload has no file name to read a version from")]
    [InlineData("  ", "Version token '{file.version}' has no value: the upload has no file name to read a version from")]
    public async Task FileVersion_WhenNoVersionCanBeRead_SaysWhatIsMissing(string fileName, string message)
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => new FileVersionToken().ResolveAsync(VersioningTestContexts.Context(fileName), null, CancellationToken.None));

        ex.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData("a_", "!.zip")]
    [InlineData("a-", "!.1.2.zip")]
    [InlineData("a.", "1x.zip")]
    [InlineData("1.", ".zip")]
    public async Task FileVersion_AHostileFileName_IsAnsweredFastNotMatchedForever(string unit, string suffix)
    {
        // As long as the cap allows: the old backtracking pattern needed seconds at 49 characters and doubled with every extra pair, so at this length it would never finish.
        var fileName = string.Concat(Enumerable.Repeat(unit, (FileVersionToken.MaxFileNameLength - suffix.Length) / unit.Length)) + suffix;

        // Building the patterns and JIT are one-time costs, not what is being timed.
        await new FileVersionToken().ResolveAsync(VersioningTestContexts.Context("Acme.1.0.0.zip"), null, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await new FileVersionToken().ResolveAsync(VersioningTestContexts.Context(fileName), null, CancellationToken.None);
        }
        catch (VersionTokenValueMissingException)
        {
        }

        fileName.Length.ShouldBeGreaterThan(190);
        watch.ElapsedMilliseconds.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task FileVersion_AFileNameLongerThanTheLimit_IsRejectedUpFront()
    {
        var fileName = new string('a', FileVersionToken.MaxFileNameLength) + ".1.0.0.zip";

        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => new FileVersionToken().ResolveAsync(VersioningTestContexts.Context(fileName), null, CancellationToken.None));

        ex.Message.ShouldBe($"Version token '{{file.version}}' has no value: the file name is {fileName.Length} characters long, the limit is 200");
    }

    [Fact]
    public async Task FileVersion_AFileNameAtTheLimit_StillWorks()
    {
        var fileName = new string('a', FileVersionToken.MaxFileNameLength - ".1.0.0.zip".Length) + ".1.0.0.zip";

        (await new FileVersionToken().ResolveAsync(VersioningTestContexts.Context(fileName), null, CancellationToken.None)).ShouldBe("1.0.0");
    }

    [Fact]
    public async Task FileVersion_OnlyAnExtension_HasNoVersion()
    {
        await Should.ThrowAsync<VersionTokenValueMissingException>(() => new FileVersionToken().ResolveAsync(VersioningTestContexts.Context(".zip"), null, CancellationToken.None));
    }

    [Fact]
    public async Task FileVersion_TakesNoArgument()
    {
        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => new FileVersionToken().ResolveAsync(VersioningTestContexts.Context("Acme.1.0.0.zip"), "x", CancellationToken.None));

        ex.Message.ShouldBe("Version token '{file.version}' cannot take the argument 'x': this token takes no argument");
    }

    [Fact]
    public async Task Input_IsTheTypedVersionTrimmed()
    {
        (await new InputToken().ResolveAsync(VersioningTestContexts.Context(input: "  3.1.4 "), null, CancellationToken.None)).ShouldBe("3.1.4");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Input_WhenNothingWasTyped_SaysSo(string input)
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => new InputToken().ResolveAsync(VersioningTestContexts.Context(input: input), null, CancellationToken.None));

        ex.Message.ShouldBe("Version token '{input}' has no value: no version was entered; type a version or choose another version rule");
    }

    [Fact]
    public async Task Input_TakesNoArgument()
    {
        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => new InputToken().ResolveAsync(VersioningTestContexts.Context(input: "1.0.0"), "x", CancellationToken.None));

        ex.Message.ShouldBe("Version token '{input}' cannot take the argument 'x': this token takes no argument");
    }

    [Theory]
    [InlineData(null, "0123456")]
    [InlineData("12", "0123456789ab")]
    [InlineData("1", "0")]
    public async Task GitSha_TakesTheFirstCharacters_SevenByDefault(string argument, string expected)
    {
        var value = await new GitShaToken().ResolveAsync(VersioningTestContexts.Context(gitSha: "0123456789ABCDEF0123456789abcdef01234567"), argument, CancellationToken.None);

        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "Version token '{git.sha}' has no value: no git commit id was supplied with the upload")]
    [InlineData("zzz1234", "Version token '{git.sha}' has no value: 'zzz1234' is not a git commit id (hexadecimal characters only)")]
    public async Task GitSha_WhenThereIsNoUsableCommitId_SaysSo(string sha, string message)
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => new GitShaToken().ResolveAsync(VersioningTestContexts.Context(gitSha: sha), null, CancellationToken.None));

        ex.Message.ShouldBe(message);
    }

    [Fact]
    public async Task GitSha_ShorterThanAsked_SaysSo()
    {
        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => new GitShaToken().ResolveAsync(VersioningTestContexts.Context(gitSha: "abc12"), "9", CancellationToken.None));

        ex.Message.ShouldBe("Version token '{git.sha}' has no value: the commit id 'abc12' is only 5 characters long, so 9 cannot be taken from it");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("-3")]
    [InlineData("seven")]
    public async Task GitSha_BadLength_IsRejected(string argument)
    {
        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => new GitShaToken().ResolveAsync(VersioningTestContexts.Context(gitSha: "abc1234"), argument, CancellationToken.None));

        ex.Message.ShouldBe($"Version token '{{git.sha}}' cannot take the argument '{argument}': the length must be a whole number from 1 to 64");
    }
}
