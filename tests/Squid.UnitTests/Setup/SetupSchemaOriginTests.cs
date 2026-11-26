using Squid.Core.Setup.Install;

namespace Squid.UnitTests.Setup;

public class SetupSchemaOriginTests
{
    private const string Base = "Host=db.example;Port=5432;Database=squid;Username=a;Password=b";

    [Fact]
    public void WasFresh_BeforeAnythingWasRecorded_IsFalse()
    {
        new SetupSchemaOrigin().WasFresh(Base).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Host=db.example;Port=5432;Database=squid;Username=other;Password=changed;Timeout=5", true)]
    [InlineData("Host=DB.EXAMPLE;Database=squid;Username=a;Password=b", true)]
    [InlineData("Host=db.example;Port=5432;Database=other;Username=a;Password=b", false)]
    [InlineData("Host=other.example;Port=5432;Database=squid;Username=a;Password=b", false)]
    [InlineData("Host=db.example;Port=5433;Database=squid;Username=a;Password=b", false)]
    public void TargetKey_IdentifiesHostPortAndDatabaseOnly(string other, bool sameTarget)
    {
        (SetupSchemaOrigin.TargetKey(other) == SetupSchemaOrigin.TargetKey(Base)).ShouldBe(sameTarget);
    }
}
