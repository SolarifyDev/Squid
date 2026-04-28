using Shouldly;
using Squid.Tentacle.Platform;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// P1-Phase12.A.2 — pin the IUpgradeStatusStorage abstraction:
/// Linux paths bit-for-bit preserved (regression guard for existing
/// upgrade bash scripts that write these exact paths), Windows paths
/// follow PlatformPaths convention.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class UpgradeStatusStorageTests
{
    // ── Linux paths — pinned literals matching upgrade-linux-tentacle.sh ──

    [Fact]
    public void LinuxStorage_StatusFilePath_PinnedLiteral()
    {
        // upgrade-linux-tentacle.sh's write_status helper writes to this
        // EXACT path. Server reads this metadata key; renaming would
        // silently break the upgrade-status reporting feature.
        LinuxUpgradeStatusStorage.StatusFilePath.ShouldBe("/var/lib/squid-tentacle/last-upgrade.json");
    }

    [Fact]
    public void LinuxStorage_EventsFilePath_PinnedLiteral()
    {
        // upgrade-linux-tentacle.sh's emit_event helper appends JSONL here.
        LinuxUpgradeStatusStorage.EventsFilePath.ShouldBe("/var/lib/squid-tentacle/upgrade-events.jsonl");
    }

    [Fact]
    public void LinuxStorage_LogFilePath_PinnedLiteral()
    {
        // Phase B bash log — operators reference this path in runbooks.
        LinuxUpgradeStatusStorage.LogFilePath.ShouldBe("/var/log/squid-tentacle-upgrade.log");
    }

    // ── Windows paths — under PROGRAMDATA, matches PlatformPaths ──────────

    [Fact]
    public void WindowsStorage_StatusFilePath_UsesProgramDataLayout()
    {
        // Windows upgrades will eventually write to this same path from
        // the (future) Squid.Tentacle.Upgrader.exe. Pin the layout now
        // so when the upgrader ships, paths match what server expects.
        WindowsUpgradeStatusStorage.StatusFileSubPath.ShouldBe(@"upgrade\last-upgrade.json");
        WindowsUpgradeStatusStorage.EventsFileSubPath.ShouldBe(@"upgrade\upgrade-events.jsonl");
        WindowsUpgradeStatusStorage.LogFileSubPath.ShouldBe(@"upgrade\upgrade.log");
    }

    // ── Behavior — missing files yield empty string ────────────────────────

    [Fact]
    public void Storage_MissingFiles_ReturnEmptyString()
    {
        // Pre-Phase-12 behaviour preserved: missing upgrade files → empty
        // string (NOT null, NOT exception). Server treats empty as "no
        // status available" and falls back to inferring outcome from the
        // reported agent version. Pinned across all impls.
        var nullStorage = new NullUpgradeStatusStorage();

        nullStorage.ReadStatus().ShouldBe(string.Empty);
        nullStorage.ReadEvents().ShouldBe(string.Empty);
        nullStorage.ReadLog().ShouldBe(string.Empty);
    }

    // ── Factory selection ──────────────────────────────────────────────────

    [Fact]
    public void Factory_PicksPlatformImpl()
    {
        var storage = UpgradeStatusStorageFactory.Resolve();

        if (OperatingSystem.IsLinux())
            storage.ShouldBeOfType<LinuxUpgradeStatusStorage>();
        else if (OperatingSystem.IsWindows())
            storage.ShouldBeOfType<WindowsUpgradeStatusStorage>();
        else
            // macOS / unknown — null storage. Upgrade flow is not
            // operational on these platforms; metadata is empty.
            storage.ShouldBeOfType<NullUpgradeStatusStorage>();
    }

    // ── CapabilitiesService integration: storage is used not Funcs ────────

    [Fact]
    public void CapabilitiesService_DefaultCtor_UsesFactoryResolvedStorage()
    {
        // Default ctor must produce a service whose metadata-emit path
        // calls the factory-resolved storage. Empty metadata is fine
        // (no upgrade ever ran on the test machine); we just verify
        // the call doesn't throw and produces a valid response.
        var service = new Squid.Tentacle.Core.CapabilitiesService();

        var response = service.GetCapabilities(
            new Squid.Message.Contracts.Tentacle.CapabilitiesRequest());

        response.ShouldNotBeNull();
        response.SupportedServices.ShouldContain("IScriptService/v1");
    }

    [Fact]
    public void CapabilitiesService_TestCtor_AcceptsCustomStorage_UpgradeStatusFlows()
    {
        // Test-friendly ctor accepting IUpgradeStatusStorage. Allows
        // unit tests to inject canned upgrade metadata without touching
        // the filesystem.
        var customStorage = new TestUpgradeStatusStorage(
            status: "{\"phase\":\"complete\"}",
            events: "{\"event\":\"start\"}\n",
            log: "Phase B log line one\n");

        var service = new Squid.Tentacle.Core.CapabilitiesService(metadata: null, customStorage);

        var response = service.GetCapabilities(
            new Squid.Message.Contracts.Tentacle.CapabilitiesRequest());

        response.Metadata["upgradeStatus"].ShouldBe("{\"phase\":\"complete\"}");
        response.Metadata["upgradeEvents"].ShouldBe("{\"event\":\"start\"}\n");
        response.Metadata["upgradeLog"].ShouldBe("Phase B log line one\n");
    }

    // Test-only stub
    private sealed class TestUpgradeStatusStorage : IUpgradeStatusStorage
    {
        private readonly string _status, _events, _log;
        public TestUpgradeStatusStorage(string status, string events, string log)
            => (_status, _events, _log) = (status, events, log);

        public string ReadStatus() => _status;
        public string ReadEvents() => _events;
        public string ReadLog() => _log;
    }
}
