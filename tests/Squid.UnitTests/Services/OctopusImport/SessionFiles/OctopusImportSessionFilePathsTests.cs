using Squid.Core.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport.SessionFiles;

public sealed class OctopusImportSessionFilePathsTests
{
    // ".." inside a name ("Octopus..export.zip") is an ordinary file name; only a whole ".." segment climbs out of the session.
    [Theory]
    [InlineData("Octopus..export.zip")]
    [InlineData("backup.v1..zip")]
    [InlineData("export.zip")]
    [InlineData("history-staging/Release/a.json")]
    public void EnsureRelative_OrdinaryPath_Accepted(string path) => Should.NotThrow(() => OctopusImportSessionFilePaths.EnsureRelative(path));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("..")]
    [InlineData("../export.zip")]
    [InlineData("history-staging/../export.zip")]
    [InlineData("history-staging/..")]
    [InlineData("/etc/passwd")]
    public void EnsureRelative_NotARelativePath_Refused(string path) => Should.Throw<ArgumentException>(() => OctopusImportSessionFilePaths.EnsureRelative(path));

    [Fact]
    public void EnsureRelative_OverLongPath_Refused() => Should.Throw<ArgumentException>(() => OctopusImportSessionFilePaths.EnsureRelative(new string('a', 513)));
}
