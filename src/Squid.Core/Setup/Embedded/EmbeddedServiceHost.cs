using Squid.Core.Settings.Setup;

namespace Squid.Core.Setup.Embedded;

/// <summary>
/// Owns the built-in PostgreSQL / Redis for the lifetime of the Squid process. One instance spans
/// both the first-run setup host and the normal API host, so a database started during setup keeps
/// running into normal operation; <see cref="DisposeAsync"/> stops whatever this process started.
/// </summary>
public sealed class EmbeddedServiceHost : IAsyncDisposable
{
    private readonly IProcessRunner _runner;
    private readonly string _dataDirectory;
    private readonly List<Func<CancellationToken, Task>> _stopActions = new();

    private EmbeddedPostgres _postgres;
    private EmbeddedRedis _redis;

    public EmbeddedServiceHost(IProcessRunner runner, string dataDirectory)
    {
        _runner = runner;
        _dataDirectory = dataDirectory;
    }

    public static bool IsPostgresAvailable(EmbeddedPostgresSettings settings) => EmbeddedBinaryLocator.FindPostgresBinDirectory(settings.BinDirectory) != null;

    public static bool IsRedisAvailable(EmbeddedRedisSettings settings) => EmbeddedBinaryLocator.FindRedisServer(settings.ServerPath) != null;

    public async Task StartConfiguredAsync(EmbeddedServicesSetting setting, CancellationToken ct)
    {
        if (setting.Postgres.Enabled) await StartPostgresAsync(setting.Postgres, ct).ConfigureAwait(false);
        if (setting.Redis.Enabled) await StartRedisAsync(setting.Redis, ct).ConfigureAwait(false);
    }

    public async Task<string> StartPostgresAsync(EmbeddedPostgresSettings settings, CancellationToken ct)
    {
        if (_postgres != null) return _postgres.ConnectionString;

        var binDirectory = EmbeddedBinaryLocator.FindPostgresBinDirectory(settings.BinDirectory, EmbeddedPostgres.ReadClusterMajorVersion(_dataDirectory))
            ?? throw new EmbeddedServiceException("Built-in PostgreSQL is not installed in this image; configure an external PostgreSQL connection string instead.");

        var postgres = new EmbeddedPostgres(_runner, settings, _dataDirectory, binDirectory, Environment.IsPrivilegedProcess);

        await postgres.StartAsync(ct).ConfigureAwait(false);

        _postgres = postgres;
        _stopActions.Add(postgres.StopAsync);

        return postgres.ConnectionString;
    }

    public async Task<string> StartRedisAsync(EmbeddedRedisSettings settings, CancellationToken ct)
    {
        if (_redis != null) return _redis.ConnectionString;

        var serverPath = EmbeddedBinaryLocator.FindRedisServer(settings.ServerPath)
            ?? throw new EmbeddedServiceException("Built-in Redis is not installed in this image; configure an external Redis connection string instead.");

        var redis = new EmbeddedRedis(_runner, settings, _dataDirectory, serverPath);

        await redis.StartAsync(ct).ConfigureAwait(false);

        _redis = redis;
        _stopActions.Add(redis.StopAsync);

        return redis.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        foreach (var stop in Enumerable.Reverse(_stopActions)) await StopQuietlyAsync(stop, timeout.Token).ConfigureAwait(false);

        _stopActions.Clear();
    }

    private static async Task StopQuietlyAsync(Func<CancellationToken, Task> stop, CancellationToken ct)
    {
        try
        {
            await stop(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to stop a built-in service cleanly");
        }
    }
}
