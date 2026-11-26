namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

/// <summary>The archive is the right format but unreadable or unsafe (for example an entry that would escape its folder when unpacked).</summary>
public sealed class PackageArchiveRejectedException(string reason, Exception inner) : InvalidOperationException(reason, inner);
