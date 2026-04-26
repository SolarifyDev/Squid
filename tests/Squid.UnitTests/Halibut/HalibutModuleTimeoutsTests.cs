using Shouldly;
using Squid.Core.Halibut;
using Xunit;

namespace Squid.UnitTests.Halibut;

/// <summary>
/// P1-E.10 (Phase-8): pin the env-var override surface for Halibut
/// timeouts. Pre-fix `HalibutTimeoutsAndLimits.RecommendedValues()` was
/// hardcoded — air-gapped operators / deployments with non-standard
/// network latency had no tunable. Phase-8 adds two operator-tunable
/// env vars (Rule 8). Constants pinned so future renames can't silently
/// brick deployment manifests that already set them.
/// </summary>
public sealed class HalibutModuleTimeoutsTests
{
    [Fact]
    public void TcpReceiveResponseTimeoutSecondsEnvVar_ConstantNamePinned()
    {
        HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar
            .ShouldBe("SQUID_HALIBUT_TCP_RECEIVE_RESPONSE_TIMEOUT_SECONDS");
    }

    [Fact]
    public void PollingRequestQueueTimeoutSecondsEnvVar_ConstantNamePinned()
    {
        HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar
            .ShouldBe("SQUID_HALIBUT_POLLING_QUEUE_TIMEOUT_SECONDS");
    }

    [Theory]
    [InlineData(null)]                  // unset
    [InlineData("")]                    // empty
    [InlineData("   ")]                 // whitespace
    [InlineData("garbage")]             // non-integer
    [InlineData("0")]                   // zero — fall back
    [InlineData("-30")]                 // negative — fall back
    public void TryParseSecondsEnv_InvalidValues_ReturnsFalse(string raw)
    {
        var ok = HalibutModule.TryParseSecondsEnv(raw, out var value);

        ok.ShouldBeFalse(customMessage: $"raw='{raw}' must NOT produce an override (caller falls back to RecommendedValues default).");
        value.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("60", 60)]
    [InlineData("600", 600)]
    [InlineData("  120  ", 120)]    // whitespace tolerated
    public void TryParseSecondsEnv_ValidPositiveInteger_ProducesTimeSpan(string raw, int expectedSeconds)
    {
        var ok = HalibutModule.TryParseSecondsEnv(raw, out var value);

        ok.ShouldBeTrue();
        value.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void BuildTimeoutsAndLimits_NoEnvVars_ReturnsRecommendedValuesUnchanged()
    {
        // Sanity: with no overrides, the recommended-values defaults are
        // preserved. Anyone running without env config gets the Halibut-
        // library-recommended baseline.
        Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, null);
        Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, null);

        var values = HalibutModule.BuildTimeoutsAndLimits();
        var defaults = global::Halibut.Diagnostics.HalibutTimeoutsAndLimits.RecommendedValues();

        values.TcpClientReceiveResponseTimeout.ShouldBe(defaults.TcpClientReceiveResponseTimeout);
        values.PollingRequestQueueTimeout.ShouldBe(defaults.PollingRequestQueueTimeout);
    }

    [Fact]
    public void BuildTimeoutsAndLimits_TcpReceiveOverride_AppliedToTcpReceiveResponseTimeout()
    {
        try
        {
            Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, "120");

            var values = HalibutModule.BuildTimeoutsAndLimits();

            values.TcpClientReceiveResponseTimeout.ShouldBe(TimeSpan.FromSeconds(120));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, null);
        }
    }

    [Fact]
    public void BuildTimeoutsAndLimits_PollingQueueOverride_AppliedToPollingRequestQueueTimeout()
    {
        try
        {
            Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, "45");

            var values = HalibutModule.BuildTimeoutsAndLimits();

            values.PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(45));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, null);
        }
    }

    [Fact]
    public void BuildTimeoutsAndLimits_BothOverrides_BothApplied_OthersKeepRecommendedValues()
    {
        try
        {
            Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, "90");
            Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, "30");

            var values = HalibutModule.BuildTimeoutsAndLimits();
            var defaults = global::Halibut.Diagnostics.HalibutTimeoutsAndLimits.RecommendedValues();

            values.TcpClientReceiveResponseTimeout.ShouldBe(TimeSpan.FromSeconds(90));
            values.PollingRequestQueueTimeout.ShouldBe(TimeSpan.FromSeconds(30));
            // Other fields untouched — env vars compose on top of defaults.
            values.TcpClientConnectTimeout.ShouldBe(defaults.TcpClientConnectTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HalibutModule.TcpReceiveResponseTimeoutSecondsEnvVar, null);
            Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, null);
        }
    }
}
