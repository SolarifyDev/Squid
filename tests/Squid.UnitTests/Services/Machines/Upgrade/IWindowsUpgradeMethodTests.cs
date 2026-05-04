using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// P1-Phase12.E.1 — pins the <see cref="IWindowsUpgradeMethod"/> interface
/// shape via a stub impl. Phase 12.E.1 is foundation-only: no concrete
/// methods (no <c>ZipUpgradeMethod</c> / <c>MsiUpgradeMethod</c>) exist
/// yet — they ship in 12.E.2. Until then, the interface contract itself is
/// the deliverable, and the most reliable contract pin is a class that
/// IMPLEMENTS the interface inside the test assembly: if the signature
/// drifts (renamed property, changed argument shape, return type widened),
/// the stub stops compiling → the test stops running, and the drift is
/// caught at compile time before any real impl breaks.
///
/// <para>Mirrors the same pattern used to pin <see cref="ILinuxUpgradeMethod"/>:
/// the actual impls (Apt/Yum/Tarball) live in <c>Methods/</c> with their
/// own behaviour tests; the interface itself is compile-time pinned by
/// having stubs in the test surface that implement it.</para>
/// </summary>
public sealed class IWindowsUpgradeMethodTests
{
    /// <summary>
    /// Stub impl — its mere existence pins the interface signature. Each
    /// member is filled with sentinel values so the round-trip tests below
    /// can verify the dispatcher reads them correctly.
    /// </summary>
    private sealed class StubWindowsUpgradeMethod : IWindowsUpgradeMethod
    {
        public string Name => "stub-method";

        public string RenderDetectAndInstall(string targetVersion)
            => $"# stub snippet for version {targetVersion}\nWrite-Host '[upgrade-method:stub-method] would install $targetVersion'";

        public bool RequiresExplicitSwap => true;
    }

    [Fact]
    public void Name_ReturnsStringFromImpl()
    {
        IWindowsUpgradeMethod method = new StubWindowsUpgradeMethod();

        method.Name.ShouldBe("stub-method");
    }

    [Fact]
    public void RenderDetectAndInstall_AcceptsTargetVersion_ReturnsString()
    {
        // The argument shape (single string `targetVersion`, return string)
        // is the part most likely to drift in a future "let's add a context
        // param" refactor — pinned here. Mirrors `ILinuxUpgradeMethod`.
        IWindowsUpgradeMethod method = new StubWindowsUpgradeMethod();

        var snippet = method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("1.6.0", Case.Sensitive);
        snippet.ShouldContain("[upgrade-method:stub-method]", Case.Sensitive);
    }

    [Fact]
    public void RequiresExplicitSwap_ReturnsBoolFromImpl()
    {
        // Mirrors ILinuxUpgradeMethod.RequiresExplicitSwap: methods like
        // tarball/zip extract to a staging dir and need an explicit swap
        // step; methods like msi/chocolatey have package-manager-managed
        // file placement and skip the swap. The bool is the dispatcher's
        // hint for which scope-phase branch to take.
        IWindowsUpgradeMethod method = new StubWindowsUpgradeMethod();

        method.RequiresExplicitSwap.ShouldBeTrue();
    }
}
