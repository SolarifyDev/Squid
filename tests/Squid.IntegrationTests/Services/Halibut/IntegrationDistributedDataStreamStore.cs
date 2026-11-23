using System.Diagnostics;
using System.Security.Cryptography;
using Halibut;
using Halibut.Queue.QueuedDataStreams;
using Halibut.Queue.Redis.MessageStorage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;
using Squid.Core.Persistence.Db;
using Squid.Message.Commands.DataStreamStorage;

namespace Squid.IntegrationTests.Services.HalibutStorage;

/// <summary>
/// Integration tier on the real Postgres: the store, its persistence and the sweep through the real container, mediator and migration. Covers what a
/// fake cannot: the transaction really rolls back, chunks really leave the database sealed, the sweep and the cascade delete really remove rows, and a
/// 48 MB stream really moves through bounded memory.
/// </summary>
public class IntegrationDistributedDataStreamStore : TestBase
{
    private const string Marker = "PLAINTEXT-MARKER-sensitive-variable-value-9f3a";

    public IntegrationDistributedDataStreamStore() : base("HalibutDataStreamStore", "squid_it_halibut_data_stream_store")
    {
    }

    private DistributedDataStreamStore Store => CurrentScope.Resolve<DistributedDataStreamStore>();

    private static byte[] Bytes(int length, int seed = 7)
    {
        var bytes = new byte[length];

        new Random(seed).NextBytes(bytes);

        return bytes;
    }

    private static async Task<byte[]> CollectAsync(IStoreDataStreamsForDistributedQueues store, Guid id)
    {
        var rehydrate = new CapturingRehydrate(id);

        await store.RehydrateDataStreams([1], [rehydrate], CancellationToken.None);

        await using var data = rehydrate.Supply();
        using var copy = new MemoryStream();

        await data.Data.CopyToAsync(copy);

        return copy.ToArray();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await OpenRawAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);

        return (long)await command.ExecuteScalarAsync();
    }

    private async Task<NpgsqlConnection> OpenRawAsync()
    {
        string connectionString = null;

        await Run<SquidDbContext>(db =>
        {
            connectionString = db.Database.GetConnectionString();
            return Task.CompletedTask;
        });

        var connection = new NpgsqlConnection(connectionString);

        await connection.OpenAsync();

        return connection;
    }

    private async Task ExecuteRawAsync(string sql, params object[] values)
    {
        await using var connection = await OpenRawAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });

        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024 - 1)]
    [InlineData(1024 * 1024)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(3 * 1024 * 1024 + 17)]
    public async Task RoundTrip_AtDefaultChunkBoundaries_IsByteExact_AndLeavesNoRowsBehind(int length)
    {
        var payload = Bytes(length);
        var stream = DataStream.FromBytes(payload);

        await Store.StoreDataStreams([stream], false, CancellationToken.None);

        (await CollectAsync(Store, stream.Id)).ShouldBe(payload);
        (await CountAsync("halibut_data_stream")).ShouldBe(0, "a collected stream is deleted with its reader");
        (await CountAsync("halibut_data_stream_chunk")).ShouldBe(0, "its chunks go with it");
    }

    [Fact]
    public async Task Store_ARequestWithSeveralStreams_BothDirections_AreRehydratedByTheirOwnIds()
    {
        var payloads = new[] { Bytes(2_500_000, 1), Bytes(0, 2), Bytes(70_000, 3) };
        var streams = payloads.Select(DataStream.FromBytes).ToList();

        await Store.StoreDataStreams(streams, true, CancellationToken.None);

        for (var i = streams.Count - 1; i >= 0; i--)
            (await CollectAsync(Store, streams[i].Id)).ShouldBe(payloads[i]);
    }

    [Fact]
    public async Task AtRest_TheRawRowsHoldNoPlaintext_AndTheKeyIsWrappedWithTheMasterKey()
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes($"{{\"Squid.Account.Password\":\"{Marker}\"}}" + new string(' ', 3000));
        var stream = DataStream.FromBytes(plaintext);

        await Store.StoreDataStreams([stream], false, CancellationToken.None);

        await using var connection = await OpenRawAsync();
        await using var chunks = new NpgsqlCommand("SELECT content FROM halibut_data_stream_chunk", connection);
        await using var reader = await chunks.ExecuteReaderAsync();
        var raw = new List<byte>();
        var rows = 0;

        while (await reader.ReadAsync()) { raw.AddRange((byte[])reader[0]); rows++; }

        await reader.CloseAsync();

        rows.ShouldBe(1);
        raw.ToArray().AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(Marker)).ShouldBe(-1, "ciphertext only: no secret may be at rest in the clear");
        raw.ToArray().AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("Password")).ShouldBe(-1);
        raw.Count.ShouldBe(plaintext.Length + DataStreamCodec.Overhead);

        await using var header = new NpgsqlCommand("SELECT protected_key, length, chunk_count FROM halibut_data_stream", connection);
        await using var headerReader = await header.ExecuteReaderAsync();
        (await headerReader.ReadAsync()).ShouldBeTrue();
        headerReader.GetString(0).ShouldStartWith("SQUID_ENCRYPTED_V2:");
        headerReader.GetInt64(1).ShouldBe(plaintext.Length);
        headerReader.GetInt32(2).ShouldBe(1);
    }

    [Fact]
    public async Task Read_AChunkChangedInTheDatabase_IsRefused_NotReturnedAsData()
    {
        var stream = DataStream.FromBytes(Bytes(100_000));
        await Store.StoreDataStreams([stream], false, CancellationToken.None);

        await ExecuteRawAsync("UPDATE halibut_data_stream_chunk SET content = set_byte(content, 40, get_byte(content, 40) # 1)");

        await Should.ThrowAsync<DataStreamUnreadableException>(() => CollectAsync(Store, stream.Id));
    }

    [Fact]
    public async Task Rehydrate_Missing_ThrowsNotFound()
    {
        var missing = Guid.NewGuid();

        var ex = await Should.ThrowAsync<DataStreamNotFoundException>(() => Store.RehydrateDataStreams([1], [new CapturingRehydrate(missing)], CancellationToken.None));

        ex.StreamId.ShouldBe(missing);
    }

    [Fact]
    public async Task Store_WhenTheWriterThrowsMidStream_RollsBackEverythingOfTheMessage()
    {
        var good = DataStream.FromBytes(Bytes(3 * 1024 * 1024));
        var failing = new DataStream(10_000_000, async (s, ct) =>
        {
            await s.WriteAsync(Bytes(2_500_000), ct);
            throw new IOException("writer failed halfway");
        });

        await Should.ThrowAsync<IOException>(() => Store.StoreDataStreams([good, failing], false, CancellationToken.None));

        (await CountAsync("halibut_data_stream")).ShouldBe(0);
        (await CountAsync("halibut_data_stream_chunk")).ShouldBe(0, "no orphaned chunk of the half-written stream");
    }

    [Fact]
    public async Task Store_CancelledMidStream_StopsAndLeavesNothing()
    {
        using var cts = new CancellationTokenSource();
        var slow = new DataStream(10_000_000, async (s, ct) =>
        {
            await s.WriteAsync(Bytes(2_500_000), ct);
            cts.Cancel();
            await s.WriteAsync(Bytes(2_500_000), ct);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Store.StoreDataStreams([slow], false, cts.Token));

        (await CountAsync("halibut_data_stream")).ShouldBe(0);
        (await CountAsync("halibut_data_stream_chunk")).ShouldBe(0);
    }

    [Fact]
    public async Task Store_IsInvisibleUntilTheWholeMessageIsStored()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var payload = Bytes(2_200_000);
        var slow = new DataStream(payload.Length, async (s, ct) =>
        {
            await s.WriteAsync(payload.AsMemory(0, 2_100_000), ct);
            started.SetResult();
            await release.Task;
            await s.WriteAsync(payload.AsMemory(2_100_000), ct);
        });

        var storing = Store.StoreDataStreams([slow], false, CancellationToken.None);
        await started.Task;

        (await CountAsync("halibut_data_stream")).ShouldBe(0, "a reader must never find half a message");
        await Should.ThrowAsync<DataStreamNotFoundException>(() => Store.RehydrateDataStreams([1], [new CapturingRehydrate(slow.Id)], CancellationToken.None));

        release.SetResult();
        await storing;

        (await CollectAsync(Store, slow.Id)).ShouldBe(payload);
    }

    [Fact]
    public async Task Store_SameIdTwice_ReplacesTheEarlierContent()
    {
        var first = new DataStream(10, (s, ct) => s.WriteAsync(Bytes(1_500_000, 1), ct).AsTask());
        var second = new DataStream(10, (s, ct) => s.WriteAsync(Bytes(300_000, 2), ct).AsTask()) { Id = first.Id };

        await Store.StoreDataStreams([first], false, CancellationToken.None);
        await Store.StoreDataStreams([second], false, CancellationToken.None);

        (await CollectAsync(Store, first.Id)).ShouldBe(Bytes(300_000, 2));
    }

    [Fact]
    public async Task Concurrent_Stores_AndCollects_DoNotMixStreams()
    {
        var payloads = Enumerable.Range(0, 8).Select(i => Bytes(4_000_000 + i * 13, i)).ToList();

        var results = await Task.WhenAll(payloads.Select(async payload =>
        {
            var stream = DataStream.FromBytes(payload);

            await Store.StoreDataStreams([stream], false, CancellationToken.None);

            return await CollectAsync(Store, stream.Id);
        }));

        for (var i = 0; i < payloads.Count; i++) results[i].ShouldBe(payloads[i]);
        (await CountAsync("halibut_data_stream")).ShouldBe(0);
    }

    [Fact]
    public async Task Sweep_RemovesOnlyStreamsOlderThanTheTtl_AndTheirChunks()
    {
        var old = DataStream.FromBytes(Bytes(2_300_000, 1));
        var recent = DataStream.FromBytes(Bytes(2_300_000, 2));
        await Store.StoreDataStreams([old], false, CancellationToken.None);
        await Store.StoreDataStreams([recent], false, CancellationToken.None);
        await ExecuteRawAsync("UPDATE halibut_data_stream SET created_date = now() - interval '2 hours' WHERE stream_id = $1", old.Id);

        var removed = 0;
        await Run<IDataStreamOrphanSweeper>(async sweeper => removed = await sweeper.SweepAsync());

        removed.ShouldBe(1);
        (await CountAsync("halibut_data_stream")).ShouldBe(1);
        (await CountAsync("halibut_data_stream_chunk")).ShouldBe(3, "only the recent stream's three chunks remain");
        (await CollectAsync(Store, recent.Id)).Length.ShouldBe(2_300_000);
        await Should.ThrowAsync<DataStreamNotFoundException>(() => Store.RehydrateDataStreams([1], [new CapturingRehydrate(old.Id)], CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_JustInsideTheTtl_KeepsTheStream()
    {
        var stream = DataStream.FromBytes(Bytes(1000));
        await Store.StoreDataStreams([stream], false, CancellationToken.None);
        await ExecuteRawAsync("UPDATE halibut_data_stream SET created_date = now() - interval '59 minutes' WHERE stream_id = $1", stream.Id);

        await Run<IDataStreamOrphanSweeper>(sweeper => sweeper.SweepAsync());

        (await CountAsync("halibut_data_stream")).ShouldBe(1);
    }

    // The seam: the command, its handler and the sweep are reachable through the real mediator and container.
    [Fact]
    public async Task SweepCommand_ThroughTheRealMediator_RemovesAnAgedOrphan()
    {
        var orphan = DataStream.FromBytes(Bytes(5000));
        await Store.StoreDataStreams([orphan], false, CancellationToken.None);
        await ExecuteRawAsync("UPDATE halibut_data_stream SET created_date = now() - interval '3 hours'");

        var response = await Run<IMediator, SweepOrphanedDataStreamsResponse>(m => m.SendAsync<SweepOrphanedDataStreamsCommand, SweepOrphanedDataStreamsResponse>(new SweepOrphanedDataStreamsCommand()));

        response.Data.Removed.ShouldBe(1);
        (await CountAsync("halibut_data_stream")).ShouldBe(0);
    }

    [Fact]
    public void TheContainer_ResolvesTheStoreAsHalibutsContract()
        => CurrentScope.Resolve<IStoreDataStreamsForDistributedQueues>().ShouldBeOfType<DistributedDataStreamStore>();

    [Fact]
    public async Task FortyEightMegabytes_RoundTripByteExact_WithMemoryBelowTwoThirdsOfThePayload()
    {
        const int total = 48 * 1024 * 1024;
        var expectedHash = HashOfGenerated(total);
        var stream = new DataStream(total, (s, ct) => GenerateAsync(s, total, ct));

        var peak = new PeakMemory();
        await peak.RunAsync(async () =>
        {
            await Store.StoreDataStreams([stream], false, CancellationToken.None);

            var rehydrate = new CapturingRehydrate(stream.Id);
            await Store.RehydrateDataStreams([1], [rehydrate], CancellationToken.None);

            await using var data = rehydrate.Supply();
            data.Data.Length.ShouldBe(total);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[84000];
            int read;

            while ((read = await data.Data.ReadAsync(buffer)) > 0) hash.AppendData(buffer, 0, read);

            hash.GetHashAndReset().ShouldBe(expectedHash, "48 MB came back byte-exact");
        });

        Console.WriteLine($"48 MB round trip: peak managed-heap growth {peak.PeakGrowthBytes / 1024 / 1024} MB");
        peak.PeakGrowthBytes.ShouldBeLessThan(total * 2 / 3, $"the payload is {total / 1024 / 1024} MB; chunked streaming must not hold it (peak growth was {peak.PeakGrowthBytes / 1024 / 1024} MB)");
        (await CountAsync("halibut_data_stream_chunk")).ShouldBe(0);
    }

    private static byte[] HashOfGenerated(int total)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[65536];
        var random = new Random(99);

        for (var remaining = total; remaining > 0; remaining -= block.Length)
        {
            random.NextBytes(block);
            hash.AppendData(block, 0, Math.Min(block.Length, remaining));
        }

        return hash.GetHashAndReset();
    }

    private static async Task GenerateAsync(Stream destination, int total, CancellationToken ct)
    {
        var block = new byte[65536];
        var random = new Random(99);

        for (var remaining = total; remaining > 0; remaining -= block.Length)
        {
            random.NextBytes(block);
            await destination.WriteAsync(block.AsMemory(0, Math.Min(block.Length, remaining)), ct);
        }
    }

    /// <summary>Samples the managed heap while the work runs; the heap before it (after a full collection) is the baseline.</summary>
    private sealed class PeakMemory
    {
        public long PeakGrowthBytes { get; private set; }

        public async Task RunAsync(Func<Task> work)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var baseline = GC.GetTotalMemory(false);
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    PeakGrowthBytes = Math.Max(PeakGrowthBytes, GC.GetTotalMemory(false) - baseline);
                    await Task.Delay(10);
                }
            });

            try { await work(); }
            finally
            {
                stop.Cancel();
                await sampler;
            }
        }
    }

    private sealed class CapturingRehydrate(Guid id) : IRehydrateDataStream
    {
        private Func<DataStreamRehydrationData> _supplier;

        public Guid Id { get; } = id;

        public long Length => 0;

        public void Rehydrate(Func<DataStreamRehydrationData> dataStreamRehydratorFactory) => _supplier = dataStreamRehydratorFactory;

        public DataStreamRehydrationData Supply() => _supplier();
    }
}
