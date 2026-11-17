using System.Collections.Concurrent;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    // The read scope of the item whose reads run on this async flow; none outside a read-ahead (catalog, configuration, closure).
    private readonly AsyncLocal<ReadScope> _readScope = new();

    // ── Read ahead, write in order ─────────────────────────────────────────

    // The reads of the next items run ahead, up to OctopusRestOptions.Concurrency of them; each item is still written and
    // committed on this loop, one after another in page order, so the bundle and the cursor are what a one-by-one read
    // produces. A read never writes, so an item abandoned or failed while reading (or read ahead and never reached)
    // leaves nothing of itself. Returns false when the chunk's time budget stopped the page part way.
    private async Task<bool> ReadAheadThenWriteInOrderAsync<TItem, TRead>(IReadOnlyList<TItem> items, ItemSteps<TItem, TRead> steps, CancellationToken ct)
    {
        using var readAhead = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reads = new List<ItemRead<TRead>>();

        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (ChunkBudgetSpent()) return false;

                StartReadsAhead(items, index, reads, item => StartItemRead(() => steps.Read(item, readAhead.Token)));
                EnterItem(reads[index].Scope);
                await WriteItemAsync(await reads[index].Read.ConfigureAwait(false), steps.Write, ct).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            await StopReadsAheadAsync(readAhead, reads).ConfigureAwait(false);
        }
    }

    // The item's own read always starts; further ones only while the chunk can still commit more than this item.
    private void StartReadsAhead<TItem, TRead>(IReadOnlyList<TItem> items, int index, List<TRead> reads, Func<TItem, TRead> start)
    {
        var end = Math.Min(items.Count, index + ReadAheadWidth());

        while (reads.Count <= index || reads.Count < end) reads.Add(start(items[reads.Count]));
    }

    // The item now being waited for is the one a deadline abandons; its requests are told apart from those of items read ahead.
    private void EnterItem(ReadScope scope)
    {
        _ctx.InItem = true;
        _ctx.CurrentItem = scope;
    }

    private int ReadAheadWidth() => _ctx.Clock.Elapsed < _options.ChunkDuration ? Math.Max(1, _options.Concurrency) : 1;

    private async Task WriteItemAsync<TRead>(Scoped<TRead> item, Func<TRead, CancellationToken, Task> write, CancellationToken ct)
    {
        AddCounts(_ctx.Pending, item.Scope.Counts);
        _ctx.PendingSkipped.AddRange(item.Scope.Skipped);

        await write(item.Value, ct).ConfigureAwait(false);

        CommitItem();
    }

    // Reads still running when the page ends (budget spent, a failure, the deadline) are cut off and waited for, so none
    // outlives the chunk or its client. Their outcome is dropped: whatever ended the page is what the caller sees.
    private static async Task StopReadsAheadAsync<TRead>(CancellationTokenSource readAhead, List<ItemRead<TRead>> reads)
    {
        await readAhead.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(reads.Select(r => (Task)r.Read)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    // ── Read scopes ────────────────────────────────────────────────────────

    // What a read records (refusals, detail counters) joins its own scope; the item adds its scope to the pending item when it is written.
    private OctopusHistoryFetchCountsDto ReadCounts => _readScope.Value?.Counts ?? _ctx.Pending;

    private List<OctopusHistoryFetchSkippedItemDto> ReadSkipped => _readScope.Value?.Skipped ?? _ctx.PendingSkipped;

    // An item's read has its own scope, known before the read ends, so the requests it makes can be named as its own.
    private ItemRead<T> StartItemRead<T>(Func<Task<T>> read)
    {
        var scope = new ReadScope(item: null);

        return new ItemRead<T>(scope, InScopeAsync(scope, read));
    }

    // A read inside an item (one of its detail lists) records into a scope of its own, and its requests stay the item's.
    private Task<Scoped<T>> InNewScopeAsync<T>(Func<Task<T>> read) => InScopeAsync(new ReadScope(_readScope.Value?.Item), read);

    // The scope and the request owner are set inside this method, so they flow into the read and never back to the caller.
    private async Task<Scoped<T>> InScopeAsync<T>(ReadScope scope, Func<Task<T>> read)
    {
        _readScope.Value = scope;
        _ctx.Client.RequestOwner = scope.Item;

        return new Scoped<T>(await read().ConfigureAwait(false), scope);
    }

    // Reads run side by side are added to the current scope in a fixed order, so the skipped items keep the one-by-one order.
    private T AddToCurrentScope<T>(Scoped<T> read)
    {
        AddCounts(ReadCounts, read.Scope.Counts);
        ReadSkipped.AddRange(read.Scope.Skipped);

        return read.Value;
    }

    // One read per key per chunk, shared by every item that needs it, whichever reads first.
    private static Task<T> ReadOnceAsync<T>(ConcurrentDictionary<string, Lazy<Task<T>>> cache, string key, Func<Task<T>> read) => cache.GetOrAdd(key, _ => new Lazy<Task<T>>(read)).Value;

    // Item is the item's own scope: itself for an item, its item's for a read inside one.
    private sealed class ReadScope(ReadScope item)
    {
        public ReadScope Item => item ?? this;
        public OctopusHistoryFetchCountsDto Counts { get; } = new();
        public List<OctopusHistoryFetchSkippedItemDto> Skipped { get; } = [];
    }

    private sealed record Scoped<T>(T Value, ReadScope Scope);

    private sealed record ItemRead<T>(ReadScope Scope, Task<Scoped<T>> Read);

    // How a page's items are read (side by side, ahead) and then written (one by one, in order).
    private sealed record ItemSteps<TItem, TRead>(Func<TItem, CancellationToken, Task<TRead>> Read, Func<TRead, CancellationToken, Task> Write);
}
