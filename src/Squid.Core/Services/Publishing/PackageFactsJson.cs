using System.Text.Json;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>Between <see cref="PackageFacts"/> and the JSON column that stores them. A null column is a package that was never inspected.</summary>
public static class PackageFactsJson
{
    public static string Serialize(PackageFacts facts) => facts == null ? null : JsonSerializer.Serialize(facts);

    public static PackageFacts Deserialize(string json) => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PackageFacts>(json);
}
