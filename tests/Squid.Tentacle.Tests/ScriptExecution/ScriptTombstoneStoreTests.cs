using System;
using System.IO;
using System.Linq;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.ScriptExecution.V2;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>Fidelity tier: HIGH — real files in a real temp directory; only the clock is injected.</summary>
public class ScriptTombstoneStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squid-tombstone-test-{Guid.NewGuid():N}");
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void WriteThenRead_RoundTrips_AndSurvivesANewStoreInstance()
    {
        NewStore().Write(Tombstone("t1", -1));

        var read = NewStore().TryRead("t1");

        read.ShouldNotBeNull();
        read.State.ShouldBe(ScriptStateV2.Completed);
        read.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public void Write_Twice_ReplacesAtomically()
    {
        var store = NewStore();

        store.Write(Tombstone("t1", 1));
        store.Write(Tombstone("t1", 2));

        store.TryRead("t1")!.ExitCode.ShouldBe(2);
        Directory.GetFiles(_dir).Length.ShouldBe(1, "no temp files may be left behind");
    }

    [Fact]
    public void TryRead_AfterRetention_IsAbsent()
    {
        var store = NewStore(retention: TimeSpan.FromHours(1));
        store.Write(Tombstone("t1", 0));

        _now += TimeSpan.FromMinutes(59);
        store.TryRead("t1").ShouldNotBeNull();

        _now += TimeSpan.FromMinutes(2);
        store.TryRead("t1").ShouldBeNull();
    }

    [Fact]
    public void Prune_RemovesExpiredFiles_AndKeepsFreshOnes()
    {
        var store = NewStore(retention: TimeSpan.FromHours(1));
        store.Write(Tombstone("old", 0));
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "old.json"), _now.UtcDateTime - TimeSpan.FromHours(2));
        store.Write(Tombstone("fresh", 0));

        var removed = store.Prune();

        removed.ShouldBe(1);
        File.Exists(Path.Combine(_dir, "old.json")).ShouldBeFalse();
        File.Exists(Path.Combine(_dir, "fresh.json")).ShouldBeTrue();
    }

    [Fact]
    public void Prune_BeyondMaxEntries_EvictsOldestFirst()
    {
        var store = NewStore(maxEntries: 3);

        for (var i = 0; i < 5; i++)
        {
            store.Write(Tombstone($"t{i}", i));
            File.SetLastWriteTimeUtc(Path.Combine(_dir, $"t{i}.json"), _now.UtcDateTime - TimeSpan.FromMinutes(10 - i));
        }

        store.Prune().ShouldBe(2);

        Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ShouldBe(new[] { "t2", "t3", "t4" });
    }

    [Fact]
    public void WriteAfterPruneInterval_PrunesWithoutAnExplicitCall()
    {
        var store = NewStore(retention: TimeSpan.FromHours(1));
        store.Write(Tombstone("old", 0));
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "old.json"), _now.UtcDateTime - TimeSpan.FromHours(2));

        _now += ScriptTombstoneStore.PruneInterval + TimeSpan.FromSeconds(1);
        store.Write(Tombstone("new", 0));

        File.Exists(Path.Combine(_dir, "old.json")).ShouldBeFalse("a write past the prune interval must trigger cleanup so the directory stays bounded");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -1)]
    public void InvalidRetentionOrMaxEntries_FallBackToDefaults(int retentionHours, int maxEntries)
    {
        var store = new ScriptTombstoneStore(_dir, TimeSpan.FromHours(retentionHours), maxEntries, () => _now);
        store.Write(Tombstone("t1", 0));

        _now += ScriptTombstoneStore.DefaultRetention - TimeSpan.FromMinutes(1);

        store.TryRead("t1").ShouldNotBeNull("a non-positive retention must not make every tombstone instantly expire");
        ScriptTombstoneStore.DefaultRetention.ShouldBe(TimeSpan.FromHours(24));
        ScriptTombstoneStore.DefaultMaxEntries.ShouldBe(10_000);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void InvalidTicketId_IsNeverWrittenOrRead(string ticketId)
    {
        var store = NewStore();

        store.Write(Tombstone(ticketId, 0));

        store.TryRead(ticketId).ShouldBeNull();
        Directory.Exists(_dir).ShouldBeFalse();
    }

    [Fact]
    public void CorruptFile_IsTreatedAsAbsent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "t1.json"), "{not json");

        NewStore().TryRead("t1").ShouldBeNull();
    }

    [Fact]
    public void DirectoryNames_ArePinned_AndLocalOneEscapesTheOrphanWorkspaceGlob()
    {
        ScriptTombstoneStore.LocalDirectoryName.ShouldBe("squid-script-tombstones");
        ScriptTombstoneStore.WorkspaceDirectoryName.ShouldBe(".squid-script-tombstones");
        ScriptTombstoneStore.LocalDirectoryName.StartsWith("squid-tentacle-", StringComparison.Ordinal).ShouldBeFalse("the orphan-workspace sweep deletes squid-tentacle-* directories by age");
    }

    private ScriptTombstoneStore NewStore(TimeSpan? retention = null, int? maxEntries = null) => new(_dir, retention, maxEntries, () => _now);

    private ScriptTombstone Tombstone(string ticketId, int exitCode) => new(ticketId, ScriptStateV2.Completed, exitCode, _now);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
