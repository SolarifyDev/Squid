using System.IO;
using Squid.Api.Setup;
using Squid.Core.Settings.Setup;

namespace Squid.UnitTests.Setup;

public sealed class SetupTokenTests
{
    [Fact]
    public void LoadOrCreate_ReusesTheTokenAcrossRestarts_UntilDeleted()
    {
        using var dir = new TempDataDirectory();
        var dataDirectory = new DataDirectorySetting(dir.Configuration());

        var first = SetupToken.LoadOrCreate(dataDirectory);
        var second = SetupToken.LoadOrCreate(dataDirectory);

        second.Value.ShouldBe(first.Value);
        first.Value.Length.ShouldBe(32);

        first.Delete();

        File.Exists(first.FilePath).ShouldBeFalse();
        SetupToken.LoadOrCreate(dataDirectory).Value.ShouldNotBe(first.Value);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("wrong", false)]
    public void Matches_RejectsAnythingButTheToken(string candidate, bool expected)
    {
        using var dir = new TempDataDirectory();
        var token = SetupToken.LoadOrCreate(new DataDirectorySetting(dir.Configuration()));

        token.Matches(candidate).ShouldBe(expected);
        token.Matches(token.Value).ShouldBeTrue();
    }

    [Fact]
    public void FileAndHeaderNames_ArePinned()
    {
        // install-squid.sh reads /data/setup-token and the web UI sends X-Setup-Token.
        SetupToken.FileName.ShouldBe("setup-token");
        SetupToken.HeaderName.ShouldBe("X-Setup-Token");
    }
}
