using Squid.Core.Setup.Install;
using Squid.Message.Models.Setup;

namespace Squid.UnitTests.Setup;

public sealed class SetupRequestValidatorTests
{
    private static readonly SetupCapabilities Everything = new(true, true);

    private static SetupInstallRequest Valid() => new()
    {
        Database = new SetupDatabaseChoice { Mode = SetupServiceMode.Builtin },
        Redis = new SetupRedisChoice { Mode = SetupServiceMode.Builtin },
        Admin = new SetupAdminAccount { UserName = "admin", Password = "longenough" },
        Server = new SetupServerUrls { ExternalUrl = "http://squid.local:8080" }
    };

    [Fact]
    public void ValidRequest_Passes() => Should.NotThrow(() => SetupRequestValidator.EnsureValid(Valid(), Everything));

    [Fact]
    public void EveryProblem_IsReportedAtOnce_KeyedByField()
    {
        var request = new SetupInstallRequest
        {
            Database = new SetupDatabaseChoice { Mode = SetupServiceMode.External, ConnectionString = "Host=;" },
            Redis = new SetupRedisChoice { Mode = SetupServiceMode.External },
            Admin = new SetupAdminAccount { UserName = " ", Password = "short" },
            Server = new SetupServerUrls { ExternalUrl = "ftp://x", CommsUrl = "not a url" }
        };

        var ex = Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(request, Everything));

        ex.Errors.Keys.ShouldBe(new[] { "database.connectionString", "redis.connectionString", "admin.userName", "admin.password", "server.externalUrl", "server.commsUrl" }, ignoreOrder: true);
    }

    [Fact]
    public void Builtin_WhenNotShipped_IsRejected()
    {
        var ex = Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(Valid(), new SetupCapabilities(false, false)));

        ex.Errors.Keys.ShouldBe(new[] { "database.mode", "redis.mode" }, ignoreOrder: true);
    }

    [Theory]
    [InlineData("Host=db;Database=squid;Username=u;Password=p", true)]
    [InlineData("Host=db;Username=u", false)]          // no database name
    [InlineData("Database=squid", false)]              // no host
    [InlineData("garbage", false)]
    public void ExternalDatabase_ConnectionStringShape(string connectionString, bool valid)
    {
        var request = Valid() with { Database = new SetupDatabaseChoice { Mode = SetupServiceMode.External, ConnectionString = connectionString } };

        if (valid) Should.NotThrow(() => SetupRequestValidator.EnsureValid(request, Everything));
        else Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(request, Everything)).Errors.Keys.ShouldContain("database.connectionString");
    }
}
