using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>Reading facts: what a recipe does with the <see cref="PackageFacts.Values"/> it is given.</summary>
public static class PackageFactsExtensions
{
    public static string Get(this PackageFacts facts, string key) => facts.Values.TryGetValue(key, out var value) ? value : null;

    public static bool Is(this PackageFacts facts, string key) => facts.Get(key) == PackageFactKeys.True;
}
