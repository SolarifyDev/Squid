using System.Collections.Generic;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Tests.Core;

public class CapabilitiesServiceTests
{
    [Fact]
    public void GetCapabilities_ReturnsSupportedServices()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.SupportedServices.ShouldContain("IScriptService/v1");
        response.SupportedServices.ShouldContain("ICapabilitiesService/v1");
    }

    [Fact]
    public void GetCapabilities_ReturnsAgentVersion()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.AgentVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetCapabilities_WithCustomMetadata_IncludesMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["kubernetes.version"] = "v1.28.0",
            ["kubernetes.platform"] = "linux/arm64"
        };

        var service = new CapabilitiesService(metadata);

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKeyAndValue("kubernetes.version", "v1.28.0");
        response.Metadata.ShouldContainKeyAndValue("kubernetes.platform", "linux/arm64");
    }

    [Fact]
    public void GetCapabilities_WithoutOverrides_IncludesRuntimeCapabilities()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldNotBeNull();
        // Runtime capabilities (os, defaultShell, etc.) are always advertised so the
        // server can pick the right script syntax without an extra round-trip.
        response.Metadata.ShouldContainKey("os");
        response.Metadata.ShouldContainKey("defaultShell");
        response.Metadata.ShouldContainKey("installedShells");
        response.Metadata.ShouldContainKey("architecture");
        response.Metadata["os"].ShouldBeOneOf("Windows", "macOS", "Linux", "Unknown");
    }

    [Fact]
    public void GetCapabilities_MetadataIsDefensiveCopy()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var service = new CapabilitiesService(metadata);

        var response1 = service.GetCapabilities(new CapabilitiesRequest());
        response1.Metadata["injected"] = "hack";

        var response2 = service.GetCapabilities(new CapabilitiesRequest());

        response2.Metadata.ShouldNotContainKey("injected");
    }

    [Fact]
    public void GetCapabilities_WithFlavorMetadata_ReturnsAllKeys()
    {
        var metadata = new Dictionary<string, string>
        {
            ["flavor"] = "KubernetesAgent",
            ["scriptPodMode"] = "ScriptPod",
            ["scriptPodImage"] = "bitnami/kubectl:latest",
            ["namespace"] = "squid-ns",
            ["workspaceIsolation"] = "SharedPVC",
            ["nfsWatchdogEnabled"] = "false",
            ["scriptPodCpuLimit"] = "500m",
            ["scriptPodMemoryLimit"] = "512Mi"
        };

        var service = new CapabilitiesService(metadata);
        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKeyAndValue("flavor", "KubernetesAgent");
        response.Metadata.ShouldContainKeyAndValue("scriptPodMode", "ScriptPod");
        response.Metadata.ShouldContainKeyAndValue("namespace", "squid-ns");
        response.Metadata.ShouldContainKeyAndValue("workspaceIsolation", "SharedPVC");
        // Runtime capabilities (os/defaultShell/installedShells/architecture/osVersion)
        // are merged in addition to the caller-supplied metadata, so the count is
        // flavor-specific keys (8) + runtime keys (5) = 13.
        response.Metadata.Count.ShouldBe(13);
        response.Metadata.ShouldContainKey("os");
        response.Metadata.ShouldContainKey("defaultShell");
    }

    [Fact]
    public void GetCapabilities_CallerOverrideTrumpsRuntimeInspector()
    {
        var metadata = new Dictionary<string, string>
        {
            ["os"] = "CustomPlatform"
        };

        var service = new CapabilitiesService(metadata);
        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata["os"].ShouldBe("CustomPlatform",
            "caller-supplied metadata must take precedence over the runtime inspector result");
    }

    // ── TailTruncateForMetadata: UTF-8 boundary safety (audit D.3 / 1.6.x fix) ─

    [Fact]
    public void TailTruncate_NullOrEmpty_ReturnsEmpty()
    {
        CapabilitiesService.TailTruncateForMetadata(null, 1000).ShouldBe(string.Empty);
        CapabilitiesService.TailTruncateForMetadata(System.Array.Empty<byte>(), 1000).ShouldBe(string.Empty);
    }

    [Fact]
    public void TailTruncate_UnderCap_ReturnsVerbatim()
    {
        var input = System.Text.Encoding.UTF8.GetBytes("short log content\n");
        var result = CapabilitiesService.TailTruncateForMetadata(input, 1000);
        result.ShouldBe("short log content\n",
            customMessage: "content under the cap must return exactly as-is, no marker, no truncation");
    }

    [Fact]
    public void TailTruncate_OverCap_PrependsMarker_PreservesTail()
    {
        // 2000-byte payload, 500-byte cap → tail-truncation kicks in.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 2000; i++) sb.Append('A');
        sb.Append("END_MARKER");
        var input = System.Text.Encoding.UTF8.GetBytes(sb.ToString());

        var result = CapabilitiesService.TailTruncateForMetadata(input, 500);

        result.ShouldStartWith("[…",
            customMessage: "over-cap results must start with the elision marker so client code can detect truncation");
        result.ShouldContain("earlier bytes truncated by CapabilitiesService cap (500)");
        result.ShouldEndWith("END_MARKER",
            customMessage: "tail must be preserved — operators care about the most-recent output (rollback details), not the Phase A banner");
    }

    [Fact]
    public void TailTruncate_MidMultiByteUtf8_AdvancesToCharBoundary_NeverCorrupted()
    {
        // Regression guard for audit D.3: previously this could throw
        // ArgumentException when GetString(index, length) landed on a
        // UTF-8 continuation byte — log capture would fail silently and
        // operators saw an empty log in UI.
        //
        // Construct a payload where a cut at a specific byte offset lands
        // exactly on a continuation byte. 4-byte emoji (U+1F389 🎉
        // = F0 9F 8E 89) gives us predictable continuation bytes.
        var leader = new string('X', 1000);          // 1000 single-byte chars
        var emoji = "🎉🎉🎉🎉🎉🎉🎉🎉";              // 8 × 4-byte = 32 bytes
        var trailer = "VISIBLE_END";
        var input = System.Text.Encoding.UTF8.GetBytes(leader + emoji + trailer);

        // Engineer the cap so `startByte = bytes.Length - (maxBytes - 128)`
        // lands on a continuation byte. Total input ≈ 1043 bytes. We want
        // startByte in the emoji run (bytes 1000-1031).
        //   startByte = 1043 - (170 - 128) = 1043 - 42 = 1001
        //   byte at 1001 = 0x9F (first continuation of emoji 0) — exactly
        //   the regression case.
        var result = CapabilitiesService.TailTruncateForMetadata(input, 170);

        result.ShouldNotBeNull();
        // Must decode without throwing — the fact that we got a string
        // back at all proves boundary advance worked.
        result.ShouldContain("VISIBLE_END",
            customMessage: "tail-preserved content must be visible; UTF-8 boundary advance must not eat trailing ASCII");
        result.ShouldStartWith("[…",
            customMessage: "marker must still be present");

        // Most importantly: no Unicode replacement character — boundary
        // advance should have skipped past continuation bytes cleanly,
        // not emitted U+FFFD.
        result.ShouldNotContain("\uFFFD",
            customMessage: "result must not contain the Unicode replacement character — boundary advance should have skipped past any continuation bytes before decoding");
    }

    [Fact]
    public void TailTruncate_AllContinuationBytesAfterCut_ReturnsMarkerPlusEmpty()
    {
        // Pathological: all bytes after the chosen cut point are
        // continuation bytes → advance loop runs off the end → keep=0.
        // Must not throw. (Unlikely in practice — real text has ASCII
        // intermixed — but defensive coding.)
        var bytes = new byte[200];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = 0x80;  // all continuation

        var result = CapabilitiesService.TailTruncateForMetadata(bytes, 150);

        result.ShouldNotBeNull();
        result.ShouldStartWith("[…",
            customMessage: "even when tail collapses to zero bytes after boundary advance, marker is still emitted so operator knows truncation happened");
    }
}
