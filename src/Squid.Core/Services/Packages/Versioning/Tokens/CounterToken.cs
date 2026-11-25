using System.Globalization;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>
/// <c>{counter}</c>: 1, 2, 3 ... per (feed, package, text rendered before the token). <c>{counter:daily}</c> starts again at 1 each UTC day. A template that renders a date
/// before the counter is therefore already per-day. While a template is only checked it yields 1 and takes nothing from the store.
/// </summary>
public sealed class CounterToken(IPackageVersionCounterStore store, TimeProvider time = null) : IVersionTokenSource
{
    public const string TokenName = "counter";

    public const string DailyArgument = "daily";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Token => TokenName;

    public async Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        var scope = BuildScope(context.PrecedingText, argument);

        if (context.DryRun) return "1";

        EnsurePackageKnown(context);

        var next = await store.NextAsync(context.FeedId, context.PackageId, scope, ct).ConfigureAwait(false);

        return next.ToString(CultureInfo.InvariantCulture);
    }

    private string BuildScope(string preceding, string argument)
    {
        if (argument == null) return preceding;

        if (!string.Equals(argument, DailyArgument, StringComparison.OrdinalIgnoreCase))
            throw new InvalidVersionTokenArgumentException(TokenName, argument, $"the only argument is '{DailyArgument}'");

        return $"{preceding}|{DailyArgument}:{_time.GetUtcNow():yyyy-MM-dd}";
    }

    private static void EnsurePackageKnown(VersionTokenContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PackageId))
            throw new VersionTokenValueMissingException(TokenName, "the package id is not known, so there is no counter to take a number from");
    }
}
