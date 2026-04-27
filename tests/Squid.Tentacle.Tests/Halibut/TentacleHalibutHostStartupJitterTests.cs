using Shouldly;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Halibut;

/// <summary>
/// P1-Phase9.15 — pin the env-var-driven polling-startup-jitter surface.
///
/// <para><b>Why this exists</b>: when the server restarts, all N polling
/// Tentacles detect the connection drop simultaneously and try to reconnect at
/// the same instant. Halibut's accept queue saturates → first wave of agents
/// gets connection-refused or timeout → deployments fail with "server
/// unreachable" even though the server is healthy.</para>
///
/// <para><b>Fix</b>: each Tentacle waits a uniformly-random [0, JitterMaxMs] ms
/// before invoking its first Poll(). Operators tune the upper bound to match
/// fleet size — for 1000 agents, 30s window means the reconnect storm spreads
/// over ~30s instead of arriving in a single second.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class TentacleHalibutHostStartupJitterTests
{
    [Fact]
    public void PollingStartupJitterEnvVar_ConstantNamePinned()
    {
        // Operators reference this in deploy scripts / Helm charts. Renaming
        // breaks the entire fleet's coordinated jitter window.
        TentacleHalibutHost.PollingStartupJitterEnvVar
            .ShouldBe("SQUID_TENTACLE_POLLING_STARTUP_JITTER_MS");
    }

    [Fact]
    public void DefaultPollingStartupJitterMs_IsZero_BackwardCompat()
    {
        // Default 0 means no jitter — pre-Phase-9.15 behaviour. Operators with
        // small fleets see no behaviour change. Big-fleet operators opt in by
        // setting the env var to N>0.
        TentacleHalibutHost.DefaultPollingStartupJitterMs.ShouldBe(0);
    }

    [Fact]
    public void MaxPollingStartupJitterMs_HasSanityCap()
    {
        // 5 minutes is a hard cap — beyond that, an operator's "jitter" is
        // essentially "delayed startup", and a fleet-wide misconfiguration
        // would mean no agents poll for 30+ minutes after restart.
        TentacleHalibutHost.MaxPollingStartupJitterMs.ShouldBe(5 * 60 * 1000);
    }

    [Theory]
    [InlineData(null,    0,     "unset → default 0")]
    [InlineData("",      0,     "empty → default 0")]
    [InlineData("   ",   0,     "whitespace → default 0")]
    [InlineData("0",     0,     "explicit 0 honoured")]
    [InlineData("500",   500,   "typical small-fleet value")]
    [InlineData("5000",  5000,  "5s — typical medium-fleet value")]
    [InlineData("30000", 30000, "30s — for 1000-agent fleets")]
    [InlineData("not-a-number", 0, "garbage → default 0 with warn")]
    [InlineData("-100",  0,     "negative → default 0 with warn")]
    [InlineData("400000", 300000, "above cap → clamped to 5min")]
    public void ParseStartupJitterMs_HandlesAllInputs(string raw, int expected, string scenario)
    {
        TentacleHalibutHost.ParseStartupJitterMs(raw).ShouldBe(expected, customMessage: scenario);
    }
}
