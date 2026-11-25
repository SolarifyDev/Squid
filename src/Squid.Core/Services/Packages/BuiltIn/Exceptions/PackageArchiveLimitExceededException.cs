namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

/// <summary>The archive is too big once unpacked (entry count, total size or compression ratio), which is how a decompression bomb looks: the walk stops at the first limit crossed.</summary>
public sealed class PackageArchiveLimitExceededException(string limit) : InvalidOperationException($"The archive is refused because it {limit}")
{
}
