namespace Squid.Message.Models.Publishing;

/// <summary>
/// What inspectors learned about a pushed package: a dictionary of typed facts (<see cref="PackageFactKeys"/>). This is all that is stored with a version and
/// served to recipes and clients; the archive's file list exists only while the push runs and is never part of it.
/// </summary>
public class PackageFacts
{
    public Dictionary<string, string> Values { get; set; } = new();
}
