using Squid.Tentacle.Commands;

namespace Squid.Tentacle.Tests.Commands;

public class DeleteInstanceSafetyTests
{
    [Theory]
    [InlineData("/etc/squid-tentacle/instances/production", "production", true)]
    [InlineData("/etc/squid-tentacle/instances/Default", "Default", true)]
    [InlineData("/etc/squid-tentacle/instances/Default/", "Default", true)]   // trailing slash
    [InlineData("/etc/squid-tentacle/instances/PRODUCTION", "production", true)] // case-insensitive
    public void IsSafeInstanceDir_MatchesInstanceName_ReturnsTrue(string dir, string instance, bool expected)
    {
        DeleteInstanceCommand.IsSafeInstanceDir(dir, instance).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/", "production")]              // root — catastrophic if deleted
    [InlineData("/etc", "production")]            // system dir
    [InlineData("/certs", "production")]           // wrong dir name
    [InlineData("", "production")]                 // empty path
    [InlineData(null, "production")]               // null path
    [InlineData("/etc/squid-tentacle/instances/production", "")]   // empty instance name
    [InlineData("/etc/squid-tentacle/instances/production", null)] // null instance name
    [InlineData("/etc/squid-tentacle/instances/staging", "production")] // wrong instance
    public void IsSafeInstanceDir_NonMatchingPath_ReturnsFalse(string dir, string instance)
    {
        DeleteInstanceCommand.IsSafeInstanceDir(dir, instance).ShouldBeFalse();
    }
}
