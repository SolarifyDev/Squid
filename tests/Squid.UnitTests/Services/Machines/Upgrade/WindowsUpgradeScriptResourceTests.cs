using System.IO;
using System.Reflection;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// P1-Phase12.E.2 — pins the embedded PowerShell upgrade-template resource
/// so a future "let's reorganise Resources/" refactor can't silently drop
/// the file from the assembly. Mirrors how the Linux side relies on
/// <c>upgrade-linux-tentacle.sh</c> being shipped as an embedded resource
/// resolved by manifest name in <c>LinuxTentacleUpgradeStrategy</c>.
///
/// <para>The Phase 12.E.3 <c>WindowsTentacleUpgradeStrategy</c> will load
/// the template by manifest name, substitute placeholders (TARGET_VERSION,
/// DOWNLOAD_URL, etc.), inject the per-method snippet from the renderer
/// chain (zip / future MSI / future Chocolatey), and dispatch the result
/// over Halibut. Phase 12.E.2 ships the template + ZipUpgradeMethod; Phase
/// 12.E.3 wires the loader.</para>
///
/// <para><b>Why pin placeholders explicitly</b>: a placeholder rename in
/// the .ps1 (e.g. <c>{{TARGET_VERSION}}</c> → <c>{{TARGETVERSION}}</c>)
/// without matching the Phase-12.E.3 strategy's substitution code would
/// produce silently-broken upgrades on the agent (the literal
/// <c>{{TARGET_VERSION}}</c> string ends up in the running script,
/// download URL is malformed, install fails with a confusing 404).
/// Pinned-by-test makes the drift compile/test-time visible.</para>
/// </summary>
public sealed class WindowsUpgradeScriptResourceTests
{
    /// <summary>
    /// Embedded resource name. Mirrors the
    /// <see cref="LinuxTentacleUpgradeStrategy"/> resource layout under
    /// <c>Squid.Core.Resources.Upgrade.upgrade-linux-tentacle.sh</c>.
    /// </summary>
    private const string ResourceName = "Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1";

    private static string LoadResource()
    {
        var asm = typeof(TentacleVersionRegistry).Assembly;

        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in assembly '{asm.GetName().Name}'. " +
                $"Available resources: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Resource_IsEmbeddedInSquidCoreAssembly()
    {
        // Resource is registered via Squid.Core.csproj's
        // `<EmbeddedResource Include="Resources\Upgrade\*" />` glob — but
        // a future "let's narrow this glob to be more explicit" refactor
        // could accidentally drop the .ps1. This test makes that compile-time
        // / test-time visible.
        var asm = typeof(TentacleVersionRegistry).Assembly;
        var resources = asm.GetManifestResourceNames();

        resources.ShouldContain(ResourceName,
            customMessage: $"Available resources: {string.Join(", ", resources)}");
    }

    [Fact]
    public void Resource_HasNonTrivialContent()
    {
        // Defends against a "ship empty file" mistake (touch upgrade-windows-tentacle.ps1
        // without writing it) — the assembly would build, the manifest would
        // include the file, but the actual upgrade dispatch would fail
        // immediately at PowerShell parse time with a confusing "input is null"
        // error on the agent.
        var content = LoadResource();

        content.Length.ShouldBeGreaterThan(500,
            customMessage: "upgrade-windows-tentacle.ps1 must be non-trivial — Phase A (download/extract) " +
                           "+ Phase B (stop/swap/start) is at least a few hundred lines of PowerShell");
    }

    [Theory]
    [InlineData("{{TARGET_VERSION}}")]
    [InlineData("{{DOWNLOAD_URL}}")]
    [InlineData("{{EXPECTED_SHA256}}")]
    [InlineData("{{INSTALL_DIR}}")]
    [InlineData("{{SERVICE_NAME}}")]
    [InlineData("{{HEALTHCHECK_URL}}")]
    [InlineData("{{INSTALL_METHODS}}")]
    public void Resource_ContainsPlaceholder(string placeholder)
    {
        // The Phase 12.E.3 WindowsTentacleUpgradeStrategy will use string
        // replacement on these EXACT tokens. A drift on either side
        // (template renames a placeholder; strategy doesn't update its
        // substitution table; or vice-versa) produces a script that contains
        // literal "{{TARGET_VERSION}}" text on the agent — download URL is
        // malformed, install fails with a confusing 404 or PowerShell parse
        // error. Pin every placeholder explicitly here.
        var content = LoadResource();

        content.ShouldContain(placeholder,
            customMessage: $"Phase-12.E.3 strategy substitution depends on this token; renaming requires updating both sides.");
    }

    [Fact]
    public void Resource_ContainsArchDetection()
    {
        // Windows arch detection: $env:PROCESSOR_ARCHITECTURE → AMD64 / ARM64.
        // Mirrors the Linux side's `uname -m` arch detection. This test pins
        // that the .ps1 has SOME form of arch detection — the exact case-style
        // is template-author choice, but the env var name is the canonical
        // Windows API.
        var content = LoadResource();

        content.ShouldContain("PROCESSOR_ARCHITECTURE",
            customMessage: "PowerShell's canonical arch-detection env var on Windows; needed to pick win-x64 vs win-arm64 in the download URL");
    }

    [Fact]
    public void Resource_DocumentsExitCodeContract()
    {
        // The Linux template documents its exit codes in the header comment
        // so operators reading the script can map a failure to a specific
        // root cause. Same operator-readability discipline applies to the
        // Windows side. We don't pin specific codes (those evolve as the
        // template grows) but we pin that the documentation block exists.
        var content = LoadResource();

        content.ShouldContain("Exit code", Case.Insensitive,
            customMessage: "the .ps1 must document its exit codes in the header so operators see them at a glance");
    }
}
