using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public class OctopusRestOptionsTests
{
    // Renaming any of these silently drops an operator's override (a private mirror allow-list, a raised cap). Hard-pin.
    [Theory]
    [InlineData(OctopusRestOptions.AllowLoopbackEnvVar, "SQUID_OCTOPUS_IMPORT_REST_ALLOW_LOOPBACK")]
    [InlineData(OctopusRestOptions.AllowedHostsEnvVar, "SQUID_OCTOPUS_IMPORT_REST_ALLOWED_HOSTS")]
    [InlineData(OctopusRestOptions.MaxResponseBytesEnvVar, "SQUID_OCTOPUS_IMPORT_REST_MAX_RESPONSE_BYTES")]
    [InlineData(OctopusRestOptions.HistoryMaxBytesEnvVar, "SQUID_OCTOPUS_IMPORT_HISTORY_MAX_BYTES")]
    [InlineData(OctopusRestOptions.ChunkSecondsEnvVar, "SQUID_OCTOPUS_IMPORT_REST_CHUNK_SECONDS")]
    [InlineData(OctopusRestOptions.MaxLogLinesPerTaskEnvVar, "SQUID_OCTOPUS_IMPORT_MAX_LOG_LINES_PER_TASK")]
    public void EnvVarNames_Pinned(string actual, string expected) => actual.ShouldBe(expected);

    [Fact]
    public void HistoryLimitsConfigurationPrefix_Pinned() => OctopusHistoryArchiveExtractionOptions.ConfigurationPrefix.ShouldBe("OctopusImport:Limits:History");

    [Fact]
    public void Defaults_AreClosed()
    {
        var options = new OctopusRestOptions();

        options.AllowLoopback.ShouldBeFalse();
        options.AllowedHosts.ShouldBeEmpty();
        options.MaxResponseBytes.ShouldBe(50L * 1024 * 1024);
        options.HistoryMaxBytes.ShouldBe(2L * 1024 * 1024 * 1024);
        options.ChunkDuration.ShouldBe(TimeSpan.FromSeconds(20));
        options.PageSize.ShouldBe(100);
        options.MaxRetries.ShouldBe(5);
        options.MaxRedirects.ShouldBe(3);
    }

    [Fact]
    public void FromEnvironment_ReadsEveryOverride()
    {
        var names = new[] { OctopusRestOptions.AllowLoopbackEnvVar, OctopusRestOptions.AllowedHostsEnvVar, OctopusRestOptions.MaxResponseBytesEnvVar, OctopusRestOptions.HistoryMaxBytesEnvVar, OctopusRestOptions.ChunkSecondsEnvVar };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(OctopusRestOptions.AllowLoopbackEnvVar, "true");
            Environment.SetEnvironmentVariable(OctopusRestOptions.AllowedHostsEnvVar, "octopus.example.com, *.corp.example");
            Environment.SetEnvironmentVariable(OctopusRestOptions.MaxResponseBytesEnvVar, "1234");
            Environment.SetEnvironmentVariable(OctopusRestOptions.HistoryMaxBytesEnvVar, "5678");
            Environment.SetEnvironmentVariable(OctopusRestOptions.ChunkSecondsEnvVar, "9");

            var options = OctopusRestOptions.FromEnvironment();

            options.AllowLoopback.ShouldBeTrue();
            options.AllowedHosts.ShouldBe(["octopus.example.com", "*.corp.example"]);
            options.MaxResponseBytes.ShouldBe(1234);
            options.HistoryMaxBytes.ShouldBe(5678);
            options.ChunkDuration.ShouldBe(TimeSpan.FromSeconds(9));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
