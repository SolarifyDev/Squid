using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Core.Settings.Security;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier (REAL Postgres) for the session file store: chunked, sealed rows; whole-file replace in one transaction; reads that
/// never mix two versions; writes refused once a session is purged; the purge serialised against writes and taking the key with it.
/// </summary>
public class OctopusImportSessionFileStoreIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string UploadPath = "/tmp/squid-it-session-files/export.zip";

    public OctopusImportSessionFileStoreIntegrationTests() : base("OctopusImportSessionFileStore", "squid_it_octopus_import_session_file_store")
    {
    }

    // The export upload holds secrets: the rows hold ciphertext under the session's own key, which is wrapped by the master key.
    [Fact]
    public async Task Write_Read_RoundTrip_StoredBytesNeverHoldThePlaintext()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        var content = Encoding.UTF8.GetBytes("password=marker-91a7c3 " + new string('x', 5000));

        var write = await WriteAsync(sessionId, "export.zip", content);

        (write.Length, write.Created).ShouldBe((content.LongLength, true));
        (await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "export.zip"))).ShouldBe(content);
        (await StoredChunksAsync(sessionId)).ShouldAllBe(chunk => !Encoding.UTF8.GetString(chunk).Contains("marker-91a7c3"));
        (await HeaderColumnAsync(sessionId, "export.zip", "encryption")).ShouldBe(OctopusImportSessionFileCodec.AesGcmV1);
        (await WrappedKeyAsync(sessionId)).ShouldStartWith("SQUID_ENCRYPTED_V2:");
    }

    [Fact]
    public async Task Overwrite_ReplacesEveryChunk_NoOrphanRows()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);

        await WriteAsync(sessionId, "octopus-history.zip", RandomNumberGenerator.GetBytes(3 * OctopusImportSessionFileCodec.ChunkBytes + 7));
        var again = await WriteAsync(sessionId, "octopus-history.zip", Encoding.UTF8.GetBytes("small"));

        (again.Created, again.PreviousLength).ShouldBe((false, 3L * OctopusImportSessionFileCodec.ChunkBytes + 7));
        (await StoredChunksAsync(sessionId)).Count.ShouldBe(1);
        (await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "octopus-history.zip"))).ShouldBe(Encoding.UTF8.GetBytes("small"));
    }

    // A replace runs in one transaction and a read in one statement: a reader racing many replaces sees one whole version each time.
    [Fact]
    public async Task ReaderDuringOverwrites_SeesOldOrNew_NeverAMix()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        var versions = new[] { Filled((byte)'a'), Filled((byte)'b') };
        await WriteAsync(sessionId, "octopus-history.zip", versions[0]);

        var writes = Task.Run(async () =>
        {
            for (var n = 1; n <= 12; n++) await WriteAsync(sessionId, "octopus-history.zip", versions[n % 2]);
        });

        while (!writes.IsCompleted)
        {
            var read = await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "octopus-history.zip"));

            (read.SequenceEqual(versions[0]) || read.SequenceEqual(versions[1])).ShouldBeTrue("a read must be one whole version");
        }

        await writes;
    }

    // A replace cut off part way (a cancelled request, a lost connection) rolls back: the previous file stays whole and readable.
    [Fact]
    public async Task WriteCancelledPartWay_PreviousFileStaysReadable()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        await WriteAsync(sessionId, "export.zip", Encoding.UTF8.GetBytes("previous"));
        using var cancel = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => Run<IOctopusImportSessionFileStore>(s => s.WriteAsync(sessionId, "export.zip", new CancellingStream(3 * OctopusImportSessionFileCodec.ChunkBytes, cancel), OctopusImportSessionFileEncoding.Identity, cancel.Token)));

        (await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "export.zip"))).ShouldBe(Encoding.UTF8.GetBytes("previous"));
        (await StoredChunksAsync(sessionId)).Count.ShouldBe(1);
    }

    // A file far larger than one chunk is streamed in as chunk rows and streamed back out identical.
    [Fact]
    public async Task LargeFile_64MB_StreamedAsChunkRows_ReadBackIdentical()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        var content = RandomNumberGenerator.GetBytes(64 * 1024 * 1024);

        await WriteAsync(sessionId, "octopus-history.zip", content);

        (await ChunkCountAsync(sessionId)).ShouldBe(64);
        await using var copy = new MemoryStream();
        var header = await Run<IOctopusImportSessionFileStore, Squid.Message.Models.OctopusImport.OctopusImportSessionFileHeader>(s => s.CopyToAsync(sessionId, "octopus-history.zip", copy));
        header.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        copy.ToArray().AsSpan().SequenceEqual(content).ShouldBeTrue();
    }

    // Staged documents are JSON, stored deflated: the database holds far less than the documents themselves.
    [Fact]
    public async Task StagedJson_StoredUnderHalfItsLength()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        var json = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 500).Select(n => $"{{\"Id\":\"Deployments-{n}\",\"EnvironmentId\":\"Environments-1\",\"ChannelId\":\"Channels-1\"}}")));

        await Run<IOctopusImportSessionFileStore>(s => s.WriteAsync(sessionId, "history-staging/Deployment/a.json", new MemoryStream(json), OctopusImportSessionFileEncoding.Deflate));

        (await StoredChunksAsync(sessionId)).Single().Length.ShouldBeLessThan(json.Length / 2);
        (await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "history-staging/Deployment/a.json"))).ShouldBe(json);
    }

    // The bundle is packed in this order, so it must be byte order (as the file system listing was), and a prefix is taken literally.
    [Fact]
    public async Task Paths_ListedInOrdinalOrder_PrefixTakenLiterally()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);

        foreach (var path in new[] { "k/b.json", "k/B.json", "k/a_b.json", "k/a%.json", "k/Z.json", "k/aa.json", "kk/x.json" }) await WriteAsync(sessionId, path, [1]);

        var all = new List<string>();
        await Run<IOctopusImportSessionFileStore>(async s => { await foreach (var file in s.ReadAllAsync(sessionId, "k/")) all.Add(file.Path); });

        all.ShouldBe(new[] { "k/b.json", "k/B.json", "k/a_b.json", "k/a%.json", "k/Z.json", "k/aa.json" }.Order(StringComparer.Ordinal).ToList());
        (await Run<IOctopusImportSessionFileStore, IReadOnlyList<Squid.Message.Models.OctopusImport.OctopusImportSessionFileHeader>>(s => s.ListAsync(sessionId, "k/a_"))).Select(h => h.Path).ShouldBe(["k/a_b.json"]);
        (await Run<IOctopusImportSessionFileStore, Squid.Message.Models.OctopusImport.OctopusImportSessionFileTotals>(s => s.TotalsAsync(sessionId, "k/"))).ShouldBe(new Squid.Message.Models.OctopusImport.OctopusImportSessionFileTotals(6, 6));
    }

    // ── Purge ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_ClosedSession_DeletesEveryFile_TheKeyAndThePassword_AndMarksItCleaned()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, "SQUID_ENCRYPTED_V2:retained");
        await WriteAsync(sessionId, "export.zip", [1, 2, 3]);
        await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, OctopusImportSessionState.Failed.ToString())));

        (await Run<IOctopusImportSessionFileStore, bool>(s => s.PurgeAndMarkCleanedAsync(sessionId, UploadPath, DateTimeOffset.UtcNow))).ShouldBeTrue();

        var stored = await StoredSessionAsync(sessionId);
        stored.TemporaryUploadCleanedAt.ShouldNotBeNull();
        (stored.TemporaryUploadPath, stored.ProtectedExportPassword).ShouldBe((null, null));
        (await WrappedKeyAsync(sessionId)).ShouldBeNull();
        (await FileCountAsync(sessionId)).ShouldBe(0);
        (await Run<IOctopusImportSessionFileStore, IReadOnlySet<Guid>>(s => s.PurgedAmongAsync([sessionId, Guid.NewGuid()]))).ShouldBe(new HashSet<Guid> { sessionId });
    }

    // Only a closed session still holding the expected upload is purged; an open one keeps every file.
    [Theory]
    [InlineData(OctopusImportSessionState.Uploaded, UploadPath)]
    [InlineData(OctopusImportSessionState.Importing, UploadPath)]
    [InlineData(OctopusImportSessionState.Failed, "/tmp/another/upload.zip")]
    public async Task Purge_OpenSessionOrOtherUpload_DeletesNothing(OctopusImportSessionState state, string uploadPath)
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        await WriteAsync(sessionId, "export.zip", [1]);
        await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state.ToString())));

        (await Run<IOctopusImportSessionFileStore, bool>(s => s.PurgeAndMarkCleanedAsync(sessionId, uploadPath, DateTimeOffset.UtcNow))).ShouldBeFalse();

        (await FileCountAsync(sessionId)).ShouldBe(1);
        (await StoredSessionAsync(sessionId)).TemporaryUploadCleanedAt.ShouldBeNull();
    }

    // The upload request stored the export, then failed (or its pod died) before registering it: the session closes without a path,
    // and the cleanup still deletes its stored files, its key and this server's copy, and marks it cleaned so other servers sweep theirs.
    [Fact]
    public async Task Cleanup_ClosedSessionWithoutRegisteredUpload_PurgesItsFilesAndLocalCopy()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, uploadPath: null);
        await WriteAsync(sessionId, "export.zip", [1, 2, 3]);
        var directory = await Run<IOctopusImportTemporaryUploadStore, string>(u => Task.FromResult(u.GetSessionDirectory(sessionId)));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "export.zip"), "password=secret");
        await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, OctopusImportSessionState.Expired.ToString())));

        await Run<IOctopusImportTemporaryUploadCleanupService>(cleanup => cleanup.EnforceCleanupAsync());

        (await StoredSessionAsync(sessionId)).TemporaryUploadCleanedAt.ShouldNotBeNull("a closed session without a registered upload must still be cleaned");
        (await FileCountAsync(sessionId)).ShouldBe(0);
        (await WrappedKeyAsync(sessionId)).ShouldBeNull();
        Directory.Exists(directory).ShouldBeFalse();
    }

    // ".." inside a name is an ordinary file name; a whole ".." segment is refused.
    [Fact]
    public async Task DotsInsideAName_Stored_ADotDotSegmentRefused()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);

        await WriteAsync(sessionId, "Octopus..export.zip", [4, 2]);

        (await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "Octopus..export.zip"))).ShouldBe([4, 2]);
        await Should.ThrowAsync<ArgumentException>(() => WriteAsync(sessionId, "history-staging/../export.zip", [1]));
    }

    // Staged documents are counted per kind folder by the database, without listing a header per document.
    [Fact]
    public async Task CountByFolder_CountsFilesPerFirstSegmentUnderThePrefix()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);

        foreach (var path in new[] { "history-staging/Release/a.json", "history-staging/Release/b.json", "history-staging/Project/c.json", "history-details/Release/d.json", "export.zip" })
            await WriteAsync(sessionId, path, [1]);

        var counts = await Run<IOctopusImportSessionFileStore, IReadOnlyDictionary<string, int>>(s => s.CountByFolderAsync(sessionId, OctopusImportSessionFilePaths.StagingPrefix));

        counts.OrderBy(c => c.Key, StringComparer.Ordinal).ShouldBe([new("Project", 1), new("Release", 2)]);
        (await Run<IOctopusImportSessionFileStore, IReadOnlyDictionary<string, int>>(s => s.CountByFolderAsync(sessionId, "history-staging/Release/"))).OrderBy(c => c.Key, StringComparer.Ordinal).ShouldBe([new("a.json", 1), new("b.json", 1)]);
    }

    // The cache sweep runs on every server only because the container starts it.
    [Fact]
    public void LocalCacheSweeper_RegisteredAsStartable() => CurrentScope.Resolve<IEnumerable<IStartable>>().OfType<OctopusImportLocalCacheSweeper>().ShouldHaveSingleItem();

    // Once purged, nothing can be stored for the session again: a chunk still running is refused, never leaves rows behind.
    [Fact]
    public async Task WriteAfterPurge_RefusedAsStagingLost_NoRowsLeft()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Expired);

        await Run<IOctopusImportSessionFileStore, bool>(s => s.PurgeAndMarkCleanedAsync(sessionId, UploadPath, DateTimeOffset.UtcNow));

        await Should.ThrowAsync<OctopusImportHistoryStagingLostException>(() => WriteAsync(sessionId, "history-staging/Release/a.json", [1]));
        (await FileCountAsync(sessionId)).ShouldBe(0);
    }

    // A session that does not exist (deleted, or never created) has nowhere to keep a file: refused the same way.
    [Fact]
    public async Task WriteForAnUnknownSession_RefusedAsStagingLost()
        => await Should.ThrowAsync<OctopusImportHistoryStagingLostException>(() => WriteAsync(Guid.NewGuid(), "export.zip", [1]));

    // A write in flight holds the session against the purge: the purge waits for it, then deletes what it wrote. No rows survive either order.
    [Fact]
    public async Task PurgeRacingAWrite_Serialised_NoRowsLeft()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Expired);
        var slow = new SlowStream(6 * OctopusImportSessionFileCodec.ChunkBytes, TimeSpan.FromMilliseconds(100));

        var write = Run<IOctopusImportSessionFileStore>(s => s.WriteAsync(sessionId, "octopus-history.zip", slow, OctopusImportSessionFileEncoding.Identity));
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var purged = await Run<IOctopusImportSessionFileStore, bool>(s => s.PurgeAndMarkCleanedAsync(sessionId, UploadPath, DateTimeOffset.UtcNow));
        await write;

        purged.ShouldBeTrue();
        (await FileCountAsync(sessionId)).ShouldBe(0);
        (await ChunkCountAsync(sessionId)).ShouldBe(0);
    }

    // ── Unreadable ──────────────────────────────────────────────────────────

    // A chunk changed in the database (or read under another master key) is never returned as the file: the read fails as unreadable.
    [Theory]
    [InlineData("tampered")]
    [InlineData("other-master-key")]
    public async Task StoredFileNotReadableIntact_Unreadable(string damage)
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        await WriteAsync(sessionId, "export.zip", Encoding.UTF8.GetBytes("export"));

        if (damage == "tampered") await Run<SquidDbContext>(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE octopus_import_session_file_chunk SET content = overlay(content placing '\\x00'::bytea from 20 for 1) WHERE session_id = {sessionId}"));

        await Should.ThrowAsync<OctopusImportSessionFileUnreadableException>(() => Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "export.zip"), damage == "other-master-key" ? OtherMasterKey : null));
    }

    // Strict mode with an empty master key (the committed default): files are stored unsealed, the session has no key, and imports keep working.
    [Fact]
    public async Task MasterKeyUnavailable_StoredUnsealed_NoSessionKey()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);

        await Run<IOctopusImportSessionFileStore>(s => s.WriteAsync(sessionId, "export.zip", new MemoryStream([1, 2]), OctopusImportSessionFileEncoding.Identity), EmptyMasterKey);

        (await HeaderColumnAsync(sessionId, "export.zip", "encryption")).ShouldBe(OctopusImportSessionFileCodec.None);
        (await WrappedKeyAsync(sessionId)).ShouldBeNull();
        (await Run<IOctopusImportSessionFileStore, byte[]>(s => s.ReadAsync(sessionId, "export.zip"), EmptyMasterKey)).ShouldBe([1, 2]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void EmptyMasterKey(ContainerBuilder builder) => builder.Register(c => new SecuritySetting(c.Resolve<Microsoft.Extensions.Configuration.IConfiguration>()) { MasterKey = string.Empty }).AsSelf().SingleInstance();

    private static void OtherMasterKey(ContainerBuilder builder) => builder.Register(c => new SecuritySetting(c.Resolve<Microsoft.Extensions.Configuration.IConfiguration>()) { MasterKey = Convert.ToBase64String(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray()) }).AsSelf().SingleInstance();

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 2 * OctopusImportSessionFileCodec.ChunkBytes + 11).ToArray();

    private Task<Squid.Message.Models.OctopusImport.OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string path, byte[] content)
        => Run<IOctopusImportSessionFileStore, Squid.Message.Models.OctopusImport.OctopusImportSessionFileWrite>(s => s.WriteAsync(sessionId, path, new MemoryStream(content), OctopusImportSessionFileEncoding.Identity));

    private async Task<Guid> SeedAsync(OctopusImportSessionState state, string protectedExportPassword = null, string uploadPath = UploadPath)
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(new OctopusImportSession { SessionId = sessionId, DestinationSpaceId = SpaceId, OwnerUserId = CurrentUsers.InternalUser.Id, State = state.ToString(), SourceSummaryJson = "{}", TemporaryUploadPath = uploadPath, ProtectedExportPassword = protectedExportPassword, DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastStateChangedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        return sessionId;
    }

    private Task<OctopusImportSession> StoredSessionAsync(Guid sessionId) => Run<SquidDbContext, OctopusImportSession>(db => db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId));

    private Task<List<byte[]>> StoredChunksAsync(Guid sessionId) => Run<SquidDbContext, List<byte[]>>(db => db.Database.SqlQuery<byte[]>($"SELECT content AS \"Value\" FROM octopus_import_session_file_chunk WHERE session_id = {sessionId}").ToListAsync());

    private async Task<int> ChunkCountAsync(Guid sessionId) => (await Run<SquidDbContext, List<int>>(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM octopus_import_session_file_chunk WHERE session_id = {sessionId}").ToListAsync())).Single();

    private async Task<int> FileCountAsync(Guid sessionId) => (await Run<SquidDbContext, List<int>>(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM octopus_import_session_file WHERE session_id = {sessionId}").ToListAsync())).Single();

    private async Task<string> WrappedKeyAsync(Guid sessionId) => (await Run<SquidDbContext, List<string>>(db => db.Database.SqlQuery<string>($"SELECT protected_file_key AS \"Value\" FROM octopus_import_session WHERE session_id = {sessionId}").ToListAsync())).Single();

    private async Task<string> HeaderColumnAsync(Guid sessionId, string path, string column)
        => (await Run<SquidDbContext, List<string>>(db => db.Database.SqlQueryRaw<string>($"SELECT {column} AS \"Value\" FROM octopus_import_session_file WHERE session_id = {{0}} AND path = {{1}}", sessionId, path).ToListAsync())).Single();

    // Cancels the write once it is past its first chunk, inside the transaction.
    private sealed class CancellingStream(long length, CancellationTokenSource cancel) : MemoryStream(new byte[length])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > OctopusImportSessionFileCodec.ChunkBytes) cancel.Cancel();

            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    // Hands out its bytes slowly, so a write holds its transaction open for a while.
    private sealed class SlowStream(long length, TimeSpan perChunk) : MemoryStream(new byte[length])
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= OctopusImportSessionFileCodec.ChunkBytes) Started.TrySetResult();

            await Task.Delay(perChunk, cancellationToken);

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
