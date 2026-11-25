namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Where a file goes in a package store. The key is the content's own hash, so the same bytes are kept once however many versions hold them.</summary>
public static class BuiltInBlobKey
{
    public static string For(string sha256, string extension) => $"sha256/{sha256[..2]}/{sha256}{extension}";
}
