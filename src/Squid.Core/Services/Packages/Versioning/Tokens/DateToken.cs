using System.Globalization;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary><c>{date}</c> or <c>{date:format}</c>: the current UTC time in a .NET custom date format, <c>yyyy.MM.dd</c> by default.</summary>
public sealed class DateToken(TimeProvider time = null) : IVersionTokenSource
{
    public const string TokenName = "date";

    public const string DefaultFormat = "yyyy.MM.dd";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        var format = argument ?? DefaultFormat;

        return Task.FromResult(Format(format));
    }

    private string Format(string format)
    {
        try
        {
            return _time.GetUtcNow().ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new InvalidVersionTokenArgumentException(TokenName, format, $"it is not a valid date format ({ex.Message})");
        }
    }
}
