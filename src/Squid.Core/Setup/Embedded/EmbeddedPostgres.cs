using Npgsql;
using Squid.Core.Settings.Setup;

namespace Squid.Core.Setup.Embedded;

/// <summary>
/// The built-in PostgreSQL of the all-in-one image: a cluster in <c>{data}/pgdata</c>, listening
/// on loopback only, password-authenticated, started and stopped through <c>pg_ctl</c>. PostgreSQL
/// refuses to run as root, so when Squid itself runs as root (the container default) every server
/// tool is run as the <c>postgres</c> OS user and the cluster files are owned by it.
/// </summary>
public sealed class EmbeddedPostgres
{
    public const string DataFolderName = "pgdata";
    public const string InitializingMarkerName = "pgdata.initializing";
    public const string OsUser = "postgres";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _runner;
    private readonly EmbeddedPostgresSettings _settings;
    private readonly string _dataDirectory;
    private readonly string _binDirectory;
    private readonly bool _runAsServiceUser;

    public EmbeddedPostgres(IProcessRunner runner, EmbeddedPostgresSettings settings, string dataDirectory, string binDirectory, bool runAsServiceUser)
    {
        _runner = runner;
        _settings = settings;
        _dataDirectory = dataDirectory;
        _binDirectory = binDirectory;
        _runAsServiceUser = runAsServiceUser;
    }

    public string ClusterDirectory => Path.Combine(_dataDirectory, DataFolderName);

    public string LogFile => Path.Combine(_dataDirectory, "logs", "postgres.log");

    private string InitializingMarker => Path.Combine(_dataDirectory, InitializingMarkerName);

    /// <summary>Major version of an existing cluster (from <c>PG_VERSION</c>), or null when there is none yet.</summary>
    public static int? ReadClusterMajorVersion(string dataDirectory)
    {
        var versionFile = Path.Combine(dataDirectory, DataFolderName, "PG_VERSION");

        if (!File.Exists(versionFile) || File.Exists(Path.Combine(dataDirectory, InitializingMarkerName))) return null;

        return int.TryParse(File.ReadAllText(versionFile).Trim(), out var major) ? major : null;
    }

    public string ConnectionString => BuildConnectionString(_settings);

    public static string BuildConnectionString(EmbeddedPostgresSettings settings) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = settings.Port,
        Database = settings.Database,
        Username = settings.UserName,
        Password = settings.Password
    }.ConnectionString;

    public async Task StartAsync(CancellationToken ct)
    {
        await InitializeClusterIfMissingAsync(ct).ConfigureAwait(false);
        await RepairOwnershipIfChangedAsync(ct).ConfigureAwait(false);
        await EnsureLogDirectoryAsync(ct).ConfigureAwait(false);

        if (await IsRunningAsync(ct).ConfigureAwait(false)) return;

        await RunToolAsync("pg_ctl", BuildStartArguments(), "start PostgreSQL", ct).ConfigureAwait(false);

        Log.Information("Built-in PostgreSQL started on 127.0.0.1:{Port}", _settings.Port);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!await IsRunningAsync(ct).ConfigureAwait(false)) return;

        await RunToolAsync("pg_ctl", new[] { "-D", ClusterDirectory, "-m", "fast", "-w", "stop" }, "stop PostgreSQL", ct).ConfigureAwait(false);

        Log.Information("Built-in PostgreSQL stopped");
    }

    /// <summary>
    /// Initialises the cluster unless a complete one exists. A marker brackets <c>initdb</c>: if the
    /// process died mid-initdb (PG_VERSION may already exist), the half-built cluster is discarded
    /// and initialised again rather than being mistaken for a working one. A cluster restored from a
    /// backup has no marker and is always kept.
    /// </summary>
    private async Task InitializeClusterIfMissingAsync(CancellationToken ct)
    {
        if (File.Exists(Path.Combine(ClusterDirectory, "PG_VERSION")) && !File.Exists(InitializingMarker)) return;

        DiscardIncompleteCluster();
        CreateClusterDirectory();
        OwnerOnlyFile.WriteAtomically(InitializingMarker, DateTime.UtcNow.ToString("O"));

        var passwordFile = WritePasswordFile();

        try
        {
            await GiveToServiceUserAsync(new[] { "-R", $"{OsUser}:{OsUser}", ClusterDirectory, passwordFile }, ct).ConfigureAwait(false);
            await RunToolAsync("initdb", BuildInitArguments(passwordFile), "initialise the PostgreSQL cluster", ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(passwordFile);
        }

        File.Delete(InitializingMarker);

        Log.Information("Initialised built-in PostgreSQL cluster at {ClusterDirectory}", ClusterDirectory);
    }

    private void DiscardIncompleteCluster()
    {
        if (!File.Exists(InitializingMarker) || !Directory.Exists(ClusterDirectory)) return;

        Log.Warning("Discarding the built-in PostgreSQL cluster left half-initialised by an interrupted start");

        Directory.Delete(ClusterDirectory, recursive: true);
    }

    /// <summary>
    /// The cluster must be owned by the <c>postgres</c> OS user or PostgreSQL refuses to start. If
    /// that user's uid changed (a rebuilt image), hand the files back before starting.
    /// </summary>
    private async Task RepairOwnershipIfChangedAsync(CancellationToken ct)
    {
        if (!_runAsServiceUser) return;

        var foreign = await _runner.RunAsync(new ProcessRunRequest("find", new[] { ClusterDirectory, "!", "-user", OsUser, "-print", "-quit" }, ToolTimeout), ct).ConfigureAwait(false);

        if (!foreign.Succeeded || string.IsNullOrWhiteSpace(foreign.Output)) return;

        Log.Warning("Built-in PostgreSQL files are not owned by the postgres user (image rebuilt?); restoring ownership");

        await GiveToServiceUserAsync(new[] { "-R", $"{OsUser}:{OsUser}", ClusterDirectory }, ct).ConfigureAwait(false);
    }

    private void CreateClusterDirectory()
    {
        Directory.CreateDirectory(ClusterDirectory);

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ClusterDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string WritePasswordFile()
    {
        var path = Path.Combine(_dataDirectory, $".pg-init-{Guid.NewGuid():N}");

        OwnerOnlyFile.WriteAtomically(path, _settings.Password);

        return path;
    }

    private async Task EnsureLogDirectoryAsync(CancellationToken ct)
    {
        var logDirectory = Path.GetDirectoryName(LogFile)!;

        Directory.CreateDirectory(logDirectory);

        await GiveToServiceUserAsync(new[] { $"{OsUser}:{OsUser}", logDirectory }, ct).ConfigureAwait(false);
    }

    private async Task GiveToServiceUserAsync(IReadOnlyList<string> chownArguments, CancellationToken ct)
    {
        if (!_runAsServiceUser) return;

        await RunAsync("chown", chownArguments, "hand the files to the postgres user", ct).ConfigureAwait(false);
    }

    private IReadOnlyList<string> BuildInitArguments(string passwordFile) =>
        new[] { "-D", ClusterDirectory, "-U", _settings.UserName, $"--pwfile={passwordFile}", "--auth=scram-sha-256", "--encoding=UTF8", "--no-locale" };

    /// <summary>Loopback only, no unix socket (its default directory does not exist in the image).</summary>
    private IReadOnlyList<string> BuildStartArguments()
    {
        var serverOptions = $"-c listen_addresses=127.0.0.1 -c port={_settings.Port} -c unix_socket_directories=''";

        return new[] { "-D", ClusterDirectory, "-l", LogFile, "-w", "-t", "60", "-o", serverOptions, "start" };
    }

    private async Task<bool> IsRunningAsync(CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(ClusterDirectory, "postmaster.pid"))) return false;

        var result = await _runner.RunAsync(BuildToolRequest("pg_ctl", new[] { "-D", ClusterDirectory, "status" }), ct).ConfigureAwait(false);

        return result.Succeeded;
    }

    private Task RunToolAsync(string tool, IReadOnlyList<string> arguments, string purpose, CancellationToken ct) => RunRequestAsync(BuildToolRequest(tool, arguments), purpose, ct);

    private Task RunAsync(string fileName, IReadOnlyList<string> arguments, string purpose, CancellationToken ct) =>
        RunRequestAsync(new ProcessRunRequest(fileName, arguments, ToolTimeout), purpose, ct);

    private async Task RunRequestAsync(ProcessRunRequest request, string purpose, CancellationToken ct)
    {
        var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new EmbeddedServiceException($"Failed to {purpose} (exit code {result.ExitCode}): {result.Output}. See {LogFile} for details.");
    }

    private ProcessRunRequest BuildToolRequest(string tool, IReadOnlyList<string> arguments)
    {
        var executable = Path.Combine(_binDirectory, tool);

        if (!_runAsServiceUser) return new ProcessRunRequest(executable, arguments, ToolTimeout);

        return new ProcessRunRequest("runuser", new[] { "-u", OsUser, "--", executable }.Concat(arguments).ToArray(), ToolTimeout);
    }
}
