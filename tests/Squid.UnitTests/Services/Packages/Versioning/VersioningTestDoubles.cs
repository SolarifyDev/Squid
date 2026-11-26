using System.Linq;
using Squid.Core.Services.Packages.Versioning;
using Squid.Core.Services.Packages.Versioning.Tokens;
using Squid.Message.Models.PackageVersioning;

namespace Squid.UnitTests.Services.Packages.Versioning;

/// <summary>A clock a test sets; local time is nine hours ahead of UTC so a token that reads local time instead of UTC gives a different day.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus9", TimeSpan.FromHours(9), "plus9", "plus9");
}

/// <summary>A clock that moves on every reading, to prove an assignment reads it once.</summary>
internal sealed class AdvancingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
{
    private int _readings;

    public int Readings => _readings;

    public override DateTimeOffset GetUtcNow() => start + step * _readings++;
}

/// <summary>Counter store in memory with the semantics of the real one: per scope, starting at 1, and it remembers every call.</summary>
internal sealed class FakeCounterStore : IPackageVersionCounterStore
{
    private readonly Dictionary<(int, string, string), long> _values = new();

    public List<(int FeedId, string PackageId, string Scope)> Calls { get; } = new();

    public Task<long> NextAsync(int feedId, string packageId, string scope, CancellationToken ct)
    {
        Calls.Add((feedId, packageId, scope));

        _values[(feedId, packageId, scope)] = _values.GetValueOrDefault((feedId, packageId, scope)) + 1;

        return Task.FromResult(_values[(feedId, packageId, scope)]);
    }
}

internal static class VersioningTestContexts
{
    public static VersionTokenContext Context(string fileName = null, string input = null, string gitSha = null, Dictionary<string, string> files = null)
    {
        return new VersionTokenContext
        {
            FeedId = 7,
            PackageId = "Acme.Web",
            FileName = fileName,
            InputVersion = input,
            GitSha = gitSha,
            TryReadTextFileAsync = files == null ? null : (path, _) => Task.FromResult(files.GetValueOrDefault(path)),
            ListRootFileNamesAsync = files == null ? null : _ => Task.FromResult<IReadOnlyList<string>>(files.Keys.Where(k => !k.Contains('/')).ToList())
        };
    }

    public static PackageVersionAssigner AssignerWith(FakeCounterStore store, TimeProvider time)
    {
        IVersionTokenSource[] sources = [new CounterToken(store), new DateToken(), new FileVersionToken(), new SourceVersionToken(), new GitShaToken(), new InputToken()];

        return new PackageVersionAssigner(sources, time);
    }
}
