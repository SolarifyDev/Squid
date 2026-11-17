using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public class OctopusRestOptionsTests
{
    // Renaming any of these silently drops an operator's override (a private mirror allow-list, a raised cap). Hard-pin.
    [Theory]
    [InlineData(OctopusRestOptions.AllowLoopbackEnvVar, "SQUID_OCTOPUS_IMPORT_REST_ALLOW_LOOPBACK")]
    [InlineData(OctopusRestOptions.AllowedHostsEnvVar, "SQUID_OCTOPUS_IMPORT_REST_ALLOWED_HOSTS")]
    [InlineData(OctopusRestOptions.MaxResponseBytesEnvVar, "SQUID_OCTOPUS_IMPORT_REST_MAX_RESPONSE_BYTES")]
    [InlineData(OctopusRestOptions.ChunkSecondsEnvVar, "SQUID_OCTOPUS_IMPORT_REST_CHUNK_SECONDS")]
    [InlineData(OctopusRestOptions.MaxLogLinesPerTaskEnvVar, "SQUID_OCTOPUS_IMPORT_MAX_LOG_LINES_PER_TASK")]
    [InlineData(OctopusRestOptions.ConcurrencyEnvVar, "SQUID_OCTOPUS_IMPORT_REST_CONCURRENCY")]
    [InlineData(OctopusRestOptions.MaxListItemsEnvVar, "SQUID_OCTOPUS_IMPORT_REST_MAX_LIST_ITEMS")]
    public void EnvVarNames_Pinned(string actual, string expected) => actual.ShouldBe(expected);

    [Fact]
    public void Defaults_AreClosed()
    {
        var options = new OctopusRestOptions();

        options.AllowLoopback.ShouldBeFalse();
        options.AllowedHosts.ShouldBeEmpty();
        options.MaxResponseBytes.ShouldBe(50L * 1024 * 1024);
        options.ChunkDuration.ShouldBe(TimeSpan.FromSeconds(20));
        options.PageSize.ShouldBe(100);
        options.MaxRetries.ShouldBe(5);
        options.MaxRedirects.ShouldBe(3);
        options.MaxListItems.ShouldBe(10_000);
        options.ChunkOverrun.ShouldBe(TimeSpan.FromMinutes(2));
        options.Concurrency.ShouldBe(8);
        OctopusRestOptions.MaxConcurrency.ShouldBe(64);
    }

    // 1 is the operator's way back to one request at a time; nonsense falls back to the default rather than to zero slots,
    // and a typo such as 10000 is held at the cap rather than opening that many requests against the Octopus server.
    [Theory]
    [InlineData("1", 1)]
    [InlineData("16", 16)]
    [InlineData("64", 64)]
    [InlineData("65", OctopusRestOptions.MaxConcurrency)]
    [InlineData("10000", OctopusRestOptions.MaxConcurrency)]
    [InlineData("99999999999", OctopusRestOptions.MaxConcurrency)]
    [InlineData("0", OctopusRestOptions.DefaultConcurrency)]
    [InlineData("-3", OctopusRestOptions.DefaultConcurrency)]
    [InlineData("many", OctopusRestOptions.DefaultConcurrency)]
    public void FromEnvironment_Concurrency(string value, int expected)
    {
        var saved = Environment.GetEnvironmentVariable(OctopusRestOptions.ConcurrencyEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(OctopusRestOptions.ConcurrencyEnvVar, value);

            OctopusRestOptions.FromEnvironment().Concurrency.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OctopusRestOptions.ConcurrencyEnvVar, saved);
        }
    }

    // A chunk that outlives its lease lets a second chunk start against the same session; the hard deadline must end it first.
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(600)]
    public void ChunkDeadline_EndsBeforeTheLeaseExpires(int chunkSeconds)
    {
        var options = new OctopusRestOptions { ChunkDuration = TimeSpan.FromSeconds(chunkSeconds) };

        options.ChunkDeadline.ShouldBe(options.ChunkDuration + options.ChunkOverrun);
        options.ChunkDeadline.ShouldBeLessThan(options.ChunkDuration + OctopusImportServerSourceService.LeaseMargin);
    }

    [Fact]
    public void FromEnvironment_ReadsEveryOverride()
    {
        var names = new[] { OctopusRestOptions.AllowLoopbackEnvVar, OctopusRestOptions.AllowedHostsEnvVar, OctopusRestOptions.MaxResponseBytesEnvVar, OctopusRestOptions.ChunkSecondsEnvVar, OctopusRestOptions.MaxListItemsEnvVar };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(OctopusRestOptions.AllowLoopbackEnvVar, "true");
            Environment.SetEnvironmentVariable(OctopusRestOptions.AllowedHostsEnvVar, "octopus.example.com, *.corp.example");
            Environment.SetEnvironmentVariable(OctopusRestOptions.MaxResponseBytesEnvVar, "1234");
            Environment.SetEnvironmentVariable(OctopusRestOptions.ChunkSecondsEnvVar, "9");
            Environment.SetEnvironmentVariable(OctopusRestOptions.MaxListItemsEnvVar, "25000");

            var options = OctopusRestOptions.FromEnvironment();

            options.AllowLoopback.ShouldBeTrue();
            options.AllowedHosts.ShouldBe(["octopus.example.com", "*.corp.example"]);
            options.MaxResponseBytes.ShouldBe(1234);
            options.ChunkDuration.ShouldBe(TimeSpan.FromSeconds(9));
            options.MaxListItems.ShouldBe(25_000);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
