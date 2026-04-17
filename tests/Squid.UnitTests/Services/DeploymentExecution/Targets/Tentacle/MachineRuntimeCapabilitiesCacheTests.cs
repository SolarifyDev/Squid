using Shouldly;
using Squid.Core.Services.DeploymentExecution.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public sealed class MachineRuntimeCapabilitiesCacheTests
{
    [Fact]
    public void Store_ThenGet_ReturnsRuntimeCapabilities()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(42, new Dictionary<string, string>
        {
            ["os"] = "Windows",
            ["osVersion"] = "10.0.19044",
            ["defaultShell"] = "pwsh",
            ["installedShells"] = "pwsh,powershell,cmd",
            ["architecture"] = "X64"
        }, agentVersion: "2.0.0");

        var caps = cache.TryGet(42);

        caps.Os.ShouldBe("Windows");
        caps.OsVersion.ShouldBe("10.0.19044");
        caps.DefaultShell.ShouldBe("pwsh");
        caps.InstalledShells.ShouldBe("pwsh,powershell,cmd");
        caps.Architecture.ShouldBe("X64");
        caps.AgentVersion.ShouldBe("2.0.0");
    }

    [Fact]
    public void TryGet_UnknownMachine_ReturnsEmpty_NotNull()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        var caps = cache.TryGet(999);

        caps.ShouldBeSameAs(MachineRuntimeCapabilities.Empty);
        caps.Os.ShouldBeEmpty();
        caps.DefaultShell.ShouldBeEmpty();
    }

    [Fact]
    public void Store_MissingKey_LeavesFieldEmpty()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(1, new Dictionary<string, string> { ["os"] = "Linux" }, agentVersion: "1.0");

        var caps = cache.TryGet(1);

        caps.Os.ShouldBe("Linux");
        caps.DefaultShell.ShouldBeEmpty();
        caps.InstalledShells.ShouldBeEmpty();
    }

    [Fact]
    public void Store_NullMetadata_DoesNotThrow()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        Should.NotThrow(() => cache.Store(1, null, "1.0"));
        cache.TryGet(1).ShouldBeSameAs(MachineRuntimeCapabilities.Empty);
    }

    [Fact]
    public void Store_Overwrite_LastWriteWins()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(1, new Dictionary<string, string> { ["os"] = "Linux" }, "1.0");
        cache.Store(1, new Dictionary<string, string> { ["os"] = "Windows" }, "2.0");

        cache.TryGet(1).Os.ShouldBe("Windows");
        cache.TryGet(1).AgentVersion.ShouldBe("2.0");
    }
}
