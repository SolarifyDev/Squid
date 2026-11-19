using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationResilienceOptionsTests
{
    // Renaming one of these silently drops every operator override that pinned it.
    [Theory]
    [InlineData(OctopusImportConfirmationResilienceOptions.TransientRetriesEnvVar, "SQUID_OCTOPUS_IMPORT_CONFIRM_TRANSIENT_RETRIES")]
    [InlineData(OctopusImportConfirmationResilienceOptions.HeartbeatIntervalSecondsEnvVar, "SQUID_OCTOPUS_IMPORT_CONFIRM_HEARTBEAT_INTERVAL_SECONDS")]
    [InlineData(OctopusImportConfirmationResilienceOptions.HeartbeatStaleSecondsEnvVar, "SQUID_OCTOPUS_IMPORT_CONFIRM_HEARTBEAT_STALE_SECONDS")]
    [InlineData(OctopusImportConfirmationResilienceOptions.ProgressIntervalMillisecondsEnvVar, "SQUID_OCTOPUS_IMPORT_CONFIRM_PROGRESS_INTERVAL_MS")]
    public void EnvVar_ConstantNamePinned(string actual, string expected)
    {
        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, null, null, 2, 30, 300)]        // unset: the defaults
    [InlineData("-1", null, null, 2, 30, 300)]        // a negative retry count falls back to the default
    [InlineData("0", null, null, 0, 30, 300)]         // retries can be turned off
    [InlineData("99", null, null, 5, 30, 300)]        // clamped to the maximum
    [InlineData("abc", "1", null, 2, 5, 300)]         // unparsable falls back; an interval below the floor is raised to it
    [InlineData(null, "60", "100", 2, 60, 180)]       // stale below three intervals is raised to three intervals
    [InlineData(null, "10", "600", 2, 10, 600)]
    public void From_DefaultsAndClamps(string retries, string interval, string stale, int expectedRetries, int expectedIntervalSeconds, int expectedStaleSeconds)
    {
        var values = new Dictionary<string, string>
        {
            [OctopusImportConfirmationResilienceOptions.TransientRetriesEnvVar] = retries,
            [OctopusImportConfirmationResilienceOptions.HeartbeatIntervalSecondsEnvVar] = interval,
            [OctopusImportConfirmationResilienceOptions.HeartbeatStaleSecondsEnvVar] = stale
        };

        var options = OctopusImportConfirmationResilienceOptions.From(name => values.GetValueOrDefault(name));

        options.TransientRetries.ShouldBe(expectedRetries);
        options.MaxAttempts.ShouldBe(expectedRetries + 1);
        options.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(expectedIntervalSeconds));
        options.HeartbeatStale.ShouldBe(TimeSpan.FromSeconds(expectedStaleSeconds));
    }

    [Theory]
    [InlineData(null, 500)]      // unset: the default
    [InlineData("abc", 500)]     // unparsable falls back to the default
    [InlineData("100", 250)]     // below the floor is raised to it: progress never writes more than four times a second
    [InlineData("2000", 2000)]
    public void ProgressInterval_DefaultsAndFloor(string configured, int expectedMilliseconds)
    {
        var options = OctopusImportConfirmationResilienceOptions.From(name => name == OctopusImportConfirmationResilienceOptions.ProgressIntervalMillisecondsEnvVar ? configured : null);

        options.ProgressInterval.ShouldBe(TimeSpan.FromMilliseconds(expectedMilliseconds));
        new OctopusImportConfirmationResilienceOptions().ProgressInterval.ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Theory]
    [InlineData(1, 0.0, 2000)]
    [InlineData(2, 0.0, 8000)]
    [InlineData(3, 0.0, 32000)]
    [InlineData(1, 0.2, 2400)]
    [InlineData(2, -0.2, 6400)]
    public void RetryDelay_BacksOffByFourWithJitter(int retry, double jitter, double expectedMilliseconds)
    {
        OctopusImportConfirmationResilienceOptions.RetryDelay(retry, jitter).TotalMilliseconds.ShouldBe(expectedMilliseconds, 0.001);
    }
}
