using Microsoft.Extensions.Configuration;
using Squid.Core.Setup;

namespace Squid.UnitTests.Setup;

public sealed class SetupRequirementTests
{
    [Theory]
    [InlineData(false, null, null, false, false)]                 // pre-existing deployment: never set up
    [InlineData(false, null, "pw", false, false)]                 // no data directory: never set up, even with a password
    [InlineData(true, "Host=db", null, false, false)]             // database given: ready
    [InlineData(true, "Host=db", "pw", false, false)]             // database + admin given: ready
    [InlineData(true, null, null, true, false)]                   // nothing given: wizard
    [InlineData(true, "", null, true, false)]                     // empty connection string counts as none
    [InlineData(true, null, "pw", true, true)]                    // admin password only: unattended built-in DB
    public void Decides(bool hasDataDirectory, string connectionString, string adminPassword, bool expectRequired, bool expectUnattended)
    {
        using var dir = new TempDataDirectory();
        var values = new Dictionary<string, string> { ["DataDirectory"] = hasDataDirectory ? dir.Path : "", ["SquidStore:ConnectionString"] = connectionString, ["Bootstrap:Admin:Password"] = adminPassword };
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();

        SetupRequirement.IsRequired(configuration).ShouldBe(expectRequired);
        SetupRequirement.IsUnattended(configuration).ShouldBe(expectUnattended);
    }
}
