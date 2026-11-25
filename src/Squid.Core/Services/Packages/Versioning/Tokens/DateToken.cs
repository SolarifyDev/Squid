using System.Globalization;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary><c>{date}</c> or <c>{date:format}</c>: the current UTC time in a .NET custom date format, <c>yyyy.MM.dd</c> by default.</summary>
public sealed class DateToken : IVersionTokenSource
{
    public const string TokenName = "date";

    public const string DefaultFormat = "yyyy.MM.dd";

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        return Task.FromResult(Format(context.Now, argument ?? DefaultFormat));
    }

    private static string Format(DateTimeOffset now, string format)
    {
        try
        {
            return now.ToUniversalTime().ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new InvalidVersionTokenArgumentException(TokenName, format, $"it is not a valid date format ({ex.Message})");
        }
    }
}
