using System.Text.Json;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportDeploymentHistoryImporter : IScopedDependency
{
    Task<OctopusImportDeploymentHistoryResult> ImportAsync(
        OctopusImportDeploymentHistoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the history unit, or updates the rows an earlier import wrote in place (same ids, children replaced only when asked).</summary>
    Task<OctopusImportDeploymentHistoryResult> UpsertAsync(OctopusImportHistoryUnit unit, CancellationToken ct = default);

    /// <summary>The only field an update of an imported release writes.</summary>
    Task UpdateReleaseNotesAsync(int releaseId, string releaseNotes, CancellationToken ct = default);
}

public sealed record OctopusImportDeploymentHistoryRequest(
    int SpaceId,
    int ProjectId,
    int ChannelId,
    int ReleaseId,
    int EnvironmentId,
    string Name,
    string Json,
    string DeployedBy,
    string DeployedById,
    string DeployedToMachineIdsJson,
    DateTimeOffset Created,
    string TaskName,
    string TaskDescription,
    DateTimeOffset QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? CompletedTime,
    string TaskState,
    string ErrorMessage,
    bool HasWarningsOrErrors,
    int DurationSeconds);

public sealed record OctopusImportDeploymentHistoryResult(int DeploymentId, int TaskId, int CreatedBy);

/// <summary>
/// Writes imported Octopus deployment history with raw SQL inside the import transaction: server task, deployment,
/// completion, and the task's children (activity tree, logs, resolved interruptions, events). Imported tasks are
/// inert: no concurrency tag, never pending interruptions, original state kept.
/// </summary>
public sealed partial class OctopusImportDeploymentHistoryImporter : IOctopusImportDeploymentHistoryImporter
{
    /// <summary>Caps the Octopus log lines written per task; past it one warning line says how many were dropped.</summary>
    public const string MaxLogLinesPerTaskEnvVar = "SQUID_OCTOPUS_IMPORT_MAX_LOG_LINES_PER_TASK";

    private const string ImportSource = "Octopus";

    private readonly SquidDbContext _dbContext;
    private readonly IRepository _repository;

    private HistoryUnitContext _ctx;

    public OctopusImportDeploymentHistoryImporter(SquidDbContext dbContext, IRepository repository)
    {
        _dbContext = dbContext;
        _repository = repository;
    }

    public Task<OctopusImportDeploymentHistoryResult> ImportAsync(OctopusImportDeploymentHistoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        return UpsertAsync(ToHistoryUnit(request), cancellationToken);
    }

    public async Task<OctopusImportDeploymentHistoryResult> UpsertAsync(OctopusImportHistoryUnit unit, CancellationToken ct = default)
    {
        Validate(unit);

        _ctx = await BuildContextAsync(unit, ct).ConfigureAwait(false);

        var taskId = await WriteServerTaskAsync(ct).ConfigureAwait(false);
        var deploymentId = await WriteDeploymentAsync(taskId, ct).ConfigureAwait(false);

        await WriteCompletionAsync(deploymentId, ct).ConfigureAwait(false);
        await WriteChildrenAsync(deploymentId, taskId, ct).ConfigureAwait(false);

        return new OctopusImportDeploymentHistoryResult(deploymentId, taskId, _ctx.ActorId);
    }

    public async Task UpdateReleaseNotesAsync(int releaseId, string releaseNotes, CancellationToken ct = default)
    {
        if (releaseId <= 0) throw new ArgumentOutOfRangeException(nameof(releaseId));

        await _dbContext.Database.ExecuteSqlRawAsync("UPDATE release SET release_notes = {0}, last_modified_date = now() WHERE id = {1}", new object[] { releaseNotes, releaseId }, ct).ConfigureAwait(false);
    }

    /// <summary>The configured per-task log line cap; an unset, unparsable or non-positive value falls back to the default.</summary>
    public static int MaxLogLinesPerTask()
        => int.TryParse(System.Environment.GetEnvironmentVariable(MaxLogLinesPerTaskEnvVar), out var value) && value > 0 ? value : OctopusImportFingerprint.DefaultMaxLogLinesPerTask;

    private static OctopusImportHistoryUnit ToHistoryUnit(OctopusImportDeploymentHistoryRequest request)
        => new()
        {
            SpaceId = request.SpaceId,
            ProjectId = request.ProjectId,
            ChannelId = request.ChannelId,
            ReleaseId = request.ReleaseId,
            EnvironmentId = request.EnvironmentId,
            Deployment = new OctopusDeploymentDto { Name = request.Name, DeployedBy = request.DeployedBy, DeployedById = request.DeployedById, DeployedToMachineIds = ParseMachineIds(request.DeployedToMachineIdsJson), Created = request.Created },
            Task = new OctopusServerTaskDto { Name = request.TaskName, Description = request.TaskDescription, QueueTime = request.QueueTime, StartTime = request.StartTime, CompletedTime = request.CompletedTime, State = request.TaskState, ErrorMessage = request.ErrorMessage, HasWarningsOrErrors = request.HasWarningsOrErrors, DurationSeconds = request.DurationSeconds }
        };

    private static List<string> ParseMachineIds(string json)
        => string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static void Validate(OctopusImportDeploymentHistoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Deployment name is required.", nameof(request.Name));
        if (string.IsNullOrWhiteSpace(request.TaskName)) throw new ArgumentException("Task name is required.", nameof(request.TaskName));
    }

    private static void Validate(OctopusImportHistoryUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Deployment);
        ArgumentNullException.ThrowIfNull(unit.Task);

        if (unit.SpaceId <= 0) throw new ArgumentOutOfRangeException(nameof(unit.SpaceId));
        if (unit.ProjectId <= 0) throw new ArgumentOutOfRangeException(nameof(unit.ProjectId));
        if (unit.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(unit.ChannelId));
        if (unit.ReleaseId <= 0) throw new ArgumentOutOfRangeException(nameof(unit.ReleaseId));
        if (unit.EnvironmentId <= 0) throw new ArgumentOutOfRangeException(nameof(unit.EnvironmentId));
        if (string.IsNullOrWhiteSpace(unit.Task.Name)) throw new ArgumentException("Task name is required.", nameof(unit));
    }
}
