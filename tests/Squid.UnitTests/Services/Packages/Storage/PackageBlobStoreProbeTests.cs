using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class PackageBlobStoreProbeTests
{
    [Fact]
    public async Task WorkingStore_Passes_AndTheProbeKeyIsRemoved()
    {
        var store = new InMemoryStore();

        var result = await PackageBlobStoreProbe.RunAsync(store, CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        store.PutKeys.ShouldHaveSingleItem().ShouldStartWith(PackageBlobStoreProbe.KeyPrefix);
        store.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreThatReturnsDifferentContent_Fails_AndStillCleansUp()
    {
        var store = new InMemoryStore { Corrupt = true };

        var result = await PackageBlobStoreProbe.RunAsync(store, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("different content");
        store.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreThatCannotWrite_FailsWithItsReason_InsteadOfThrowing()
    {
        var store = new InMemoryStore { FailWrites = true };

        var result = await PackageBlobStoreProbe.RunAsync(store, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("disk is full");
    }

    [Fact]
    public async Task StoreThatCannotDelete_StillReportsWhatTheWriteAndReadFound()
    {
        var store = new InMemoryStore { FailDeletes = true };

        var result = await PackageBlobStoreProbe.RunAsync(store, CancellationToken.None);

        result.Success.ShouldBeTrue("the probe wrote and read fine; a leftover probe file is harmless");
    }

    private sealed class InMemoryStore : IPackageBlobStore
    {
        public Dictionary<string, byte[]> Blobs { get; } = new();
        public List<string> PutKeys { get; } = new();
        public bool Corrupt { get; set; }
        public bool FailWrites { get; set; }
        public bool FailDeletes { get; set; }

        public string Kind => "Memory";

        public async Task PutAsync(string key, Stream content, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("disk is full");

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);

            Blobs[key] = buffer.ToArray();
            PutKeys.Add(key);
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
        {
            if (!Blobs.TryGetValue(key, out var bytes)) throw new PackageBlobNotFoundException(key);

            return Task.FromResult<Stream>(new MemoryStream(Corrupt ? [1, 2, 3] : bytes));
        }

        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Blobs.ContainsKey(key));

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            if (FailDeletes) throw new IOException("read-only");

            Blobs.Remove(key);

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListKeysAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            foreach (var key in Blobs.Keys) yield return key;
        }
    }
}
