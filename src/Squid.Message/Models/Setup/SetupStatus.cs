namespace Squid.Message.Models.Setup;

/// <summary>
/// Answer to <c>GET /api/setup/status</c>. The normal API host always answers
/// <c>Required = false</c>; only the first-run setup host answers <c>true</c>.
/// </summary>
public sealed record SetupStatus
{
    public bool Required { get; init; }
    public bool BuiltinDatabaseAvailable { get; init; }
    public bool BuiltinRedisAvailable { get; init; }
    public string SuggestedExternalUrl { get; init; } = string.Empty;
    public int DefaultPollingPort { get; init; }

    /// <summary>Non-empty when the data directory sits in an anonymous Docker volume (lost on container re-creation).</summary>
    public string DataVolumeWarning { get; init; }
}
