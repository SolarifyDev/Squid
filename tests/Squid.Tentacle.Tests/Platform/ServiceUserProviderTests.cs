using Shouldly;
using Squid.Tentacle.Platform;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// P1-Phase12.A.3 — pin the cross-platform service-user abstraction.
///
/// <para><b>Why this exists</b>: pre-Phase-12 the agent had Linux-only
/// service-user logic split across two files: <c>ServiceCommand.DetectServiceUser</c>
/// shells to <c>getent passwd squid-tentacle</c> + <c>InstanceOwnershipHandover</c>
/// has its own Func-injectable seams for the same conceptual operations.
/// Windows Tentacle support needs different identity primitives (LocalSystem
/// default, optional LSA-granted user) so the right move is to consolidate
/// both call sites onto a single typed contract.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ServiceUserProviderTests
{
    // ── DefaultServiceUser literals — pinned per platform ──────────────────

    [Fact]
    public void LinuxProvider_DefaultServiceUser_IsSquidTentacle()
    {
        // Pinned literal — install-tentacle.sh creates this exact user
        // (`useradd -r squid-tentacle`). Renaming would silently break
        // every existing operator install.
        new LinuxServiceUserProvider().DefaultServiceUser.ShouldBe("squid-tentacle");
    }

    [Fact]
    public void WindowsProvider_DefaultServiceUser_IsEmpty_MeaningLocalSystem()
    {
        // On Windows, sc.exe with no obj= argument creates the service
        // running as LocalSystem. Returning empty string signals to the
        // service-host layer "use the platform default identity"
        // — operator can override with --username/--password later.
        new WindowsServiceUserProvider().DefaultServiceUser.ShouldBe(string.Empty);
    }

    [Fact]
    public void NullProvider_DefaultServiceUser_IsEmpty()
    {
        new NullServiceUserProvider().DefaultServiceUser.ShouldBe(string.Empty);
    }

    // ── ServiceUserExists semantics ────────────────────────────────────────

    [Fact]
    public void NullProvider_ServiceUserExists_AlwaysFalse()
    {
        // Macos/dev/test fallback — no service-user notion at all.
        new NullServiceUserProvider().ServiceUserExists("squid-tentacle").ShouldBeFalse();
    }

    [Fact]
    public void WindowsProvider_ServiceUserExists_EmptyStringMeansLocalSystem_True()
    {
        // Empty user = "use platform default" (LocalSystem on Windows) →
        // platform-default ALWAYS exists. Pin this contract so the
        // ownership-handover layer doesn't refuse to proceed.
        new WindowsServiceUserProvider().ServiceUserExists(string.Empty).ShouldBeTrue();
    }

    // ── IsRoot semantics ───────────────────────────────────────────────────

    [Fact]
    public void NullProvider_IsRoot_AlwaysFalse()
    {
        // Conservative default — handover always short-circuits on Null.
        new NullServiceUserProvider().IsRunningElevated().ShouldBeFalse();
    }

    [Fact]
    public void Factory_PicksPlatformImpl()
    {
        var provider = ServiceUserProviderFactory.Resolve();

        if (OperatingSystem.IsLinux())
            provider.ShouldBeOfType<LinuxServiceUserProvider>();
        else if (OperatingSystem.IsWindows())
            provider.ShouldBeOfType<WindowsServiceUserProvider>();
        else
            provider.ShouldBeOfType<NullServiceUserProvider>();
    }

    // ── TrySetOwnership semantics — Null + Windows are no-ops ──────────────

    [Fact]
    public void NullProvider_TrySetOwnership_AlwaysReturnsTrue()
    {
        // No-op success — caller's check "if (!provider.TrySetOwnership(...)) Log.Warning(...)"
        // shouldn't spam warnings on a platform that doesn't have ownership concept.
        new NullServiceUserProvider().TrySetOwnership(path: "/tmp/x", "anyone").ShouldBeTrue();
    }

    [Fact]
    public void WindowsProvider_TrySetOwnership_NoOpReturnsTrue()
    {
        // Windows uses ACLs not Unix-style chown. The
        // FilePermissionManager (Phase-12.A.1) handles ACL hardening; this
        // provider's TrySetOwnership is a no-op for Windows so the same
        // shared call site works on both platforms without branching.
        new WindowsServiceUserProvider().TrySetOwnership(path: "C:\\temp", "anyone").ShouldBeTrue();
    }
}
