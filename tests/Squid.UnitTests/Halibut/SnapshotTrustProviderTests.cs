using System.Linq;
using Squid.Core.Halibut;

namespace Squid.UnitTests.Halibut;

public class SnapshotTrustProviderTests
{
    [Fact]
    public void TrustOnly_ReplacesTheWholeList()
    {
        var provider = new SnapshotTrustProvider();

        provider.TrustOnly(new[] { "A", "B" });
        provider.TrustOnly(new[] { "B", "C" });

        provider.IsTrusted("A").ShouldBeFalse();
        provider.IsTrusted("B").ShouldBeTrue();
        provider.IsTrusted("C").ShouldBeTrue();
        provider.ToArray().OrderBy(x => x).ShouldBe(new[] { "B", "C" });
    }

    [Fact]
    public void AddAndRemove_ChangeOnlyThatThumbprint()
    {
        var provider = new SnapshotTrustProvider();

        provider.TrustOnly(new[] { "A" });
        provider.Add("B");
        provider.Remove("A");

        provider.IsTrusted("A").ShouldBeFalse();
        provider.IsTrusted("B").ShouldBeTrue();
    }

    [Fact]
    public void IsTrusted_IsCaseInsensitive_LikeHalibutsDefaultProvider()
    {
        var provider = new SnapshotTrustProvider();

        provider.TrustOnly(new[] { "abcdef" });

        provider.IsTrusted("ABCDEF").ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyThumbprints_AreNeverTrusted(string thumbprint)
    {
        var provider = new SnapshotTrustProvider();

        provider.TrustOnly(new[] { thumbprint, "A" });
        provider.Add(thumbprint);

        provider.IsTrusted(thumbprint).ShouldBeFalse();
        provider.ToArray().ShouldBe(new[] { "A" });
    }

    [Fact]
    public void Version_IncreasesOnEverySwap()
    {
        var provider = new SnapshotTrustProvider();

        var initial = provider.Version;

        provider.TrustOnly(new[] { "A" });
        provider.Add("B");
        provider.Remove("B");

        provider.Version.ShouldBe(initial + 3);
    }

    [Fact]
    public async Task ConcurrentReaders_NeverObserveAPartialList()
    {
        var provider = new SnapshotTrustProvider();
        var first = new[] { "A1", "A2", "A3", "A4" };
        var second = new[] { "B1", "B2", "B3", "B4" };

        provider.TrustOnly(first);

        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
                provider.TrustOnly(i % 2 == 0 ? second : first);
        });

        var torn = 0;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                var snapshot = provider.ToArray();

                if (snapshot.Length != 4 || snapshot.Select(x => x[0]).Distinct().Count() != 1) Interlocked.Increment(ref torn);
            }
        })).ToArray();

        await Task.WhenAll(readers);
        stop.Cancel();
        await writer;

        torn.ShouldBe(0, "a reader saw a mix of two lists; the swap is not atomic");
    }
}
