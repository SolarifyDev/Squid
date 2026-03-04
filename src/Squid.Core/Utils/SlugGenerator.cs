using System.Text.RegularExpressions;

namespace Squid.Core.Utils;

public static class SlugGenerator
{
    public static string Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        return Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
    }
}
