namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Limits and network policy for the Octopus REST source. Operators override them through the environment
/// variables named here; tests pin every name, because renaming one silently drops an operator's override.
/// </summary>
public sealed class OctopusRestOptions
{
    public const string AllowLoopbackEnvVar = "SQUID_OCTOPUS_IMPORT_REST_ALLOW_LOOPBACK";
    public const string AllowedHostsEnvVar = "SQUID_OCTOPUS_IMPORT_REST_ALLOWED_HOSTS";
    public const string MaxResponseBytesEnvVar = "SQUID_OCTOPUS_IMPORT_REST_MAX_RESPONSE_BYTES";
    public const string HistoryMaxBytesEnvVar = "SQUID_OCTOPUS_IMPORT_HISTORY_MAX_BYTES";
    public const string ChunkSecondsEnvVar = "SQUID_OCTOPUS_IMPORT_REST_CHUNK_SECONDS";
    public const string MaxLogLinesPerTaskEnvVar = OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTaskEnvVar;

    public const long DefaultMaxResponseBytes = 50L * 1024 * 1024;
    public const long DefaultHistoryMaxBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultChunkSeconds = 20;

    // Network policy
    public bool AllowLoopback { get; init; }
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    // Limits
    public long MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public long HistoryMaxBytes { get; init; } = DefaultHistoryMaxBytes;
    public TimeSpan ChunkDuration { get; init; } = TimeSpan.FromSeconds(DefaultChunkSeconds);

    // Fixed transport settings
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);
    public int PageSize { get; init; } = 100;
    public int MaxRetries { get; init; } = 5;
    public int MaxRedirects { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    public static OctopusRestOptions FromEnvironment()
        => new()
        {
            AllowLoopback = ReadBool(AllowLoopbackEnvVar),
            AllowedHosts = ReadList(AllowedHostsEnvVar),
            MaxResponseBytes = ReadPositiveLong(MaxResponseBytesEnvVar, DefaultMaxResponseBytes),
            HistoryMaxBytes = ReadPositiveLong(HistoryMaxBytesEnvVar, DefaultHistoryMaxBytes),
            ChunkDuration = TimeSpan.FromSeconds(ReadPositiveLong(ChunkSecondsEnvVar, DefaultChunkSeconds))
        };

    private static bool ReadBool(string name) => bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value;

    private static long ReadPositiveLong(string name, long fallback) => long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static IReadOnlyList<string> ReadList(string name)
        => (Environment.GetEnvironmentVariable(name) ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
