using Squid.Tentacle.Halibut;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Halibut;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleHalibutHostUriTests
{
    [Fact]
    public void ResolvePollUri_FallsBack_When_SubscriptionUri_Is_Missing()
    {
        var uri = TentacleHalibutHost.ResolvePollUri("abc123", null);

        uri.ShouldBe(new Uri("poll://abc123/"));
    }

    [Fact]
    public void ResolvePollUri_Uses_ServerReturned_SubscriptionUri()
    {
        var uri = TentacleHalibutHost.ResolvePollUri("ignored", "poll://server-sub/");

        uri.ShouldBe(new Uri("poll://server-sub/"));
    }

    // ── P1-T.14 (Phase-8) — malformed server-returned subscriptionUri ────────
    //
    // Pre-fix the constructor `new Uri(subscriptionUri)` threw
    // `UriFormatException` on malformed input. The tentacle calls this from
    // its startup polling registrar — a buggy server release sending
    // garbage in `subscriptionUri` would crash every fresh-registering
    // agent on startup, with no clean recovery path.
    //
    // Fix: catch UriFormatException, log a warning naming the offending
    // value, fall back to the deterministic `poll://{subscriptionId}/`
    // form. The agent still polls (using its own subscription id); the
    // operator sees the warning and can chase the server bug.

    [Fact]
    public void ResolvePollUri_FallsBackOnMalformedServerUri_NoStartupCrash()
    {
        // Pre-fix this would throw UriFormatException → tentacle startup
        // crash. Post-fix it returns the deterministic fallback.
        var uri = TentacleHalibutHost.ResolvePollUri("abc123", "not a uri");

        uri.ShouldBe(new Uri("poll://abc123/"),
            customMessage:
                "T.14 — malformed server `subscriptionUri` must NOT crash the tentacle. " +
                "Fall back to the locally-constructed poll URI so polling still starts.");
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("://broken")]
    [InlineData("bad")]                      // control char
    [InlineData("ht!tp://server")]                  // illegal scheme char
    public void ResolvePollUri_Variants_AllFallBackCleanly(string malformedUri)
    {
        var uri = TentacleHalibutHost.ResolvePollUri("sub-x", malformedUri);

        uri.ShouldBe(new Uri("poll://sub-x/"));
    }

    [Fact]
    public void ResolvePollUri_RelativeUri_FallsBack()
    {
        // Relative URIs (no scheme) construct fine via `new Uri(...)` but
        // are useless to Halibut. Treat as malformed for the fallback path.
        var uri = TentacleHalibutHost.ResolvePollUri("sub-y", "/just/a/path");

        uri.IsAbsoluteUri.ShouldBeTrue(
            customMessage: "fallback Uri must be absolute (poll://{subId}/) — relative URIs from server are unusable.");
    }
}
