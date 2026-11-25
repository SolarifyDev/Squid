using System.Globalization;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>
/// <c>{counter}</c>: 1, 2, 3 ... per (feed, package, text rendered before the token). It takes no argument. To count per day put a date before it, e.g.
/// <c>{date:yyyy.MM.dd}.{counter}</c>: the rendered date is part of the scope, so the count starts again each day and the version never repeats.
/// While a template is only checked it yields 1 and takes nothing from the store.
/// </summary>
public sealed class CounterToken(IPackageVersionCounterStore store) : IVersionTokenSource
{
    public const string TokenName = "counter";

    public string Token => TokenName;

    public async Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        EnsureNoArgument(argument);

        if (context.DryRun) return "1";

        var packageId = RequirePackageId(context);
        var next = await store.NextAsync(context.FeedId, packageId, context.PrecedingText, ct).ConfigureAwait(false);

        return next.ToString(CultureInfo.InvariantCulture);
    }

    private static void EnsureNoArgument(string argument)
    {
        if (argument != null)
            throw new InvalidVersionTokenArgumentException(TokenName, argument, "this token takes no argument; to count per day put a date before it, for example {date:yyyy.MM.dd}.{counter}");
    }

    private static string RequirePackageId(VersionTokenContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PackageId))
            throw new VersionTokenValueMissingException(TokenName, "the package id is not known, so there is no counter to take a number from");

        return context.PackageId.Trim();
    }
}
