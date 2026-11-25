namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class PackageTooLargeException(long limitBytes) : InvalidOperationException($"The package is larger than the {limitBytes} byte ({limitBytes / (1024 * 1024)} MiB) limit for the built-in feed")
{
    public long LimitBytes { get; } = limitBytes;
}
