using Squid.Core.Settings.Setup;
using StackExchange.Redis;

namespace Squid.Core.Setup.Embedded;

/// <summary>
/// The built-in Redis of the all-in-one image: loopback only, password protected, append-only
/// persistence in <c>{data}/redis</c>. Configured through a generated <c>redis.conf</c> (owner-only)
/// so the password never appears on a command line.
/// </summary>
public sealed class EmbeddedRedis
{
    public const string DataFolderName = "redis";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly EmbeddedRedisSettings _settings;
    private readonly string _dataDirectory;
    private readonly string _serverPath;

    public EmbeddedRedis(IProcessRunner runner, EmbeddedRedisSettings settings, string dataDirectory, string serverPath)
    {
        _runner = runner;
        _settings = settings;
        _dataDirectory = dataDirectory;
        _serverPath = serverPath;
    }

    public string StorageDirectory => Path.Combine(_dataDirectory, DataFolderName);

    public string ConfigFile => Path.Combine(StorageDirectory, "redis.conf");

    public string PidFile => Path.Combine(StorageDirectory, "redis.pid");

    public string ConnectionString => BuildConnectionString(_settings);

    public static string BuildConnectionString(EmbeddedRedisSettings settings) =>
        $"127.0.0.1:{settings.Port},password={settings.Password},ssl=False,abortConnect=False,syncTimeout=50000,allowAdmin=true";

    public async Task StartAsync(CancellationToken ct)
    {
        if (await RespondsAsync(ct).ConfigureAwait(false)) return;

        WriteConfig();

        var result = await _runner.RunAsync(new ProcessRunRequest(_serverPath, new[] { ConfigFile }, ToolTimeout), ct).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new EmbeddedServiceException($"Failed to start Redis (exit code {result.ExitCode}): {result.Output}");

        await WaitUntilReadyAsync(ct).ConfigureAwait(false);

        Log.Information("Built-in Redis started on 127.0.0.1:{Port}", _settings.Port);
    }

    /// <summary>
    /// Asks Redis to shut down (it flushes the append-only file on SIGTERM) and waits for it to
    /// exit. The pid file is only trusted while it still names a redis-server process, so a stale
    /// file after a crash can never signal an unrelated process that reused the pid.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var pid = ReadPid();

        if (pid == null || !IsRedisProcess(pid.Value)) return;

        await _runner.RunAsync(new ProcessRunRequest("kill", new[] { "-TERM", pid.Value.ToString() }, ToolTimeout), ct).ConfigureAwait(false);

        await WaitForExitAsync(pid.Value, ct).ConfigureAwait(false);

        Log.Information("Built-in Redis stopped");
    }

    private static bool IsRedisProcess(int pid)
    {
        var commandLine = $"/proc/{pid}/cmdline";

        // Without /proc (not Linux) the owner of a pid cannot be confirmed — never signal it.
        if (!File.Exists(commandLine)) return false;

        return File.ReadAllText(commandLine).Contains("redis-server", StringComparison.Ordinal);
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline && ProcessExists(pid)) await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
    }

    private void WriteConfig()
    {
        Directory.CreateDirectory(StorageDirectory);
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "logs"));

        OwnerOnlyFile.WriteAtomically(ConfigFile, BuildConfig());
    }

    private string BuildConfig() => string.Join('\n',
        "bind 127.0.0.1",
        $"port {_settings.Port}",
        "protected-mode yes",
        $"requirepass {_settings.Password}",
        $"dir {StorageDirectory}",
        "appendonly yes",
        "daemonize yes",
        $"pidfile {PidFile}",
        $"logfile {Path.Combine(_dataDirectory, "logs", "redis.log")}",
        string.Empty);

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await RespondsAsync(ct).ConfigureAwait(false)) return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        throw new EmbeddedServiceException($"Redis did not accept connections on 127.0.0.1:{_settings.Port} within {ReadyTimeout.TotalSeconds:0}s");
    }

    private async Task<bool> RespondsAsync(CancellationToken ct)
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = true;
        options.ConnectTimeout = 1000;
        options.ConnectRetry = 0;

        try
        {
            await using var connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
            await connection.GetDatabase().PingAsync().ConfigureAwait(false);

            return true;
        }
        catch (RedisConnectionException)
        {
            return false;
        }
    }

    private int? ReadPid()
    {
        if (!File.Exists(PidFile)) return null;

        return int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid) ? pid : null;
    }
}
