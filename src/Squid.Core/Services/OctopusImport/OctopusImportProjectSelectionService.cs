using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Which Octopus projects an import reads and imports. One mechanism for both sources: the projects are listed (from the server before
/// a session exists, or from a session's source), the user's choice is stored on the session, and the fetch and the planning read it
/// from there — never from a request. Without a selection the whole space is imported, as before.
/// </summary>
public interface IOctopusImportProjectSelectionService : IScopedDependency
{
    Task<OctopusImportProjectListDto> ListServerProjectsAsync(int spaceId, OctopusServerConnection connection, CancellationToken ct = default);

    Task<OctopusImportProjectListDto> ListSessionProjectsAsync(Guid sessionId, int spaceId, CancellationToken ct = default);

    /// <param name="projectIds">The chosen Octopus project ids; null imports the whole space.</param>
    Task<OctopusImportSessionDto> SelectProjectsAsync(Guid sessionId, int spaceId, IReadOnlyCollection<string> projectIds, CancellationToken ct = default);
}

public sealed class OctopusImportProjectSelectionService(IOctopusRestProjectLister lister, IOctopusImportPlanningPipeline planningPipeline, IOctopusImportSourceMapDataProvider sourceMap, IOctopusImportSessionDataProvider sessionDataProvider, IOctopusImportSessionService sessionService, ICurrentUser currentUser) : IOctopusImportProjectSelectionService
{
    /// <summary>The most projects one selection may name.</summary>
    public const int MaxSelectedProjects = 5000;

    private static readonly OctopusImportSessionState[] ListableStates = [OctopusImportSessionState.Extracted, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated];

    private static readonly OctopusImportSessionState[] SelectableStates = [OctopusImportSessionState.Uploaded, .. ListableStates];

    // ── Listing ────────────────────────────────────────────────────────────

    public async Task<OctopusImportProjectListDto> ListServerProjectsAsync(int spaceId, OctopusServerConnection connection, CancellationToken ct = default)
    {
        var listing = await lister.ListProjectsAsync(connection, ct).ConfigureAwait(false);
        var prior = await sourceMap.LoadAsync(spaceId, ct).ConfigureAwait(false);

        return ToProjectList(listing.Groups, listing.Projects, SameInstallationOnly(prior, listing.InstallationId), null);
    }

    // Octopus ids are sequential per installation: what another installation recorded says nothing about this server's projects.
    private static OctopusImportPriorMappings SameInstallationOnly(OctopusImportPriorMappings prior, string installationId)
        => OctopusImportFillEvidences.IsAnotherInstallation(installationId, prior) ? OctopusImportPriorMappings.Empty : prior;

    public async Task<OctopusImportProjectListDto> ListSessionProjectsAsync(Guid sessionId, int spaceId, CancellationToken ct = default)
    {
        var session = await LoadOwnedSessionAsync(sessionId, spaceId, ListableStates, ct).ConfigureAwait(false);
        var graph = await planningPipeline.BuildGraphAsync(session.TemporaryUploadPath, ct).ConfigureAwait(false);
        var prior = await sourceMap.LoadAsync(spaceId, ct).ConfigureAwait(false);

        return ToProjectList(GroupsOf(graph), ProjectsOf(graph), prior, OctopusImportSourceSummaries.Parse(session.SourceSummaryJson)?.SelectedProjectIds);
    }

    private static List<OctopusRestProjectGroupItem> GroupsOf(OctopusResourceGraph graph)
        => CurrentNodes(graph, OctopusResourceKind.ProjectGroup).Select(n => new OctopusRestProjectGroupItem(n.SourceId, n.Name)).ToList();

    private static List<OctopusRestProjectItem> ProjectsOf(OctopusResourceGraph graph)
        => CurrentNodes(graph, OctopusResourceKind.Project).Select(n => n.GetSource<OctopusProjectDto>()).Where(p => p != null).Select(p => new OctopusRestProjectItem(p.Id, p.Name, p.ProjectGroupId, p.IsDisabled)).ToList();

    private static IEnumerable<OctopusResourceNode> CurrentNodes(OctopusResourceGraph graph, OctopusResourceKind kind)
        => graph.Resources.Where(r => r.Kind == kind && !r.IsHistorical).GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

    // "Imported before" is advisory: the source map's live rows, compared as every source-map key is (export suffix stripped).
    private static OctopusImportProjectListDto ToProjectList(IReadOnlyList<OctopusRestProjectGroupItem> groups, IReadOnlyList<OctopusRestProjectItem> projects, OctopusImportPriorMappings prior, List<string> selected)
        => new()
        {
            Groups = groups.Select(g => new OctopusImportProjectGroupOptionDto { Id = g.Id, Name = g.Name }).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Projects = projects.Select(p => new OctopusImportProjectOptionDto { Id = p.Id, Name = p.Name, ProjectGroupId = p.ProjectGroupId, IsDisabled = p.IsDisabled, PreviouslyImported = prior.TryGet(nameof(OctopusResourceKind.Project), p.Id, out _) }).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SelectedProjectIds = selected
        };

    // ── Selection ──────────────────────────────────────────────────────────

    public async Task<OctopusImportSessionDto> SelectProjectsAsync(Guid sessionId, int spaceId, IReadOnlyCollection<string> projectIds, CancellationToken ct = default)
    {
        var ids = NormalizeSelection(projectIds);

        EnsureWellFormed(ids);

        var session = await LoadOwnedSessionAsync(sessionId, spaceId, SelectableStates, ct).ConfigureAwait(false);
        var summary = OctopusImportSourceSummaries.Parse(session.SourceSummaryJson);

        EnsureNoActiveChunk(summary, sessionId);
        EnsureWithinRead(summary, ids, sessionId);

        return await sessionService.UpdateProjectSelectionAsync(sessionId, spaceId, ids, session.DataVersion, ct).ConfigureAwait(false);
    }

    /// <summary>Trimmed, distinct (ignoring case) and ordered; null stays null (the whole space).</summary>
    public static List<string> NormalizeSelection(IReadOnlyCollection<string> projectIds)
        => projectIds?.Select(id => id?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    // An id that is not one is refused; one the source does not hold simply matches nothing (the fetch and the plan intersect).
    public static void EnsureWellFormed(IReadOnlyCollection<string> ids)
    {
        if (ids == null) return;

        if (ids.Count == 0)
            throw new OctopusImportProjectSelectionInvalidException(OctopusImportProjectSelectionProblem.Empty, "Choose at least one Octopus project to import.");

        if (ids.Count > MaxSelectedProjects)
            throw new OctopusImportProjectSelectionInvalidException(OctopusImportProjectSelectionProblem.TooMany, $"At most {MaxSelectedProjects} Octopus projects can be chosen for one import.");

        if (!ids.All(OctopusRestPathPolicy.IsValidId))
            throw new OctopusImportProjectSelectionInvalidException(OctopusImportProjectSelectionProblem.InvalidId, "An Octopus project id in the selection is not a valid Octopus id.");
    }

    // A fetch chunk reads the selection it was claimed with; it is not changed under a running one.
    private static void EnsureNoActiveChunk(OctopusImportSourceSummaryDto summary, Guid sessionId)
    {
        if (summary?.HistoryFetch?.LeaseExpiresAt > DateTimeOffset.UtcNow)
            throw new OctopusImportSessionConcurrencyException(sessionId);
    }

    /// <summary>
    /// Once a server session started reading, only projects it reads can be chosen: a project it never read cannot be planned. Reading
    /// the whole space (no selection) reads them all; reading a selection reads only those, so a new selection must be part of it.
    /// </summary>
    public static void EnsureWithinRead(OctopusImportSourceSummaryDto summary, IReadOnlyCollection<string> ids, Guid sessionId)
    {
        var current = summary?.SelectedProjectIds;

        if (summary?.HistoryFetch?.StartedAt == null || current == null) return;

        var read = current.Select(OctopusImportPriorMappings.Normalize).ToHashSet(StringComparer.Ordinal);

        if (ids == null || !ids.All(id => read.Contains(OctopusImportPriorMappings.Normalize(id))))
            throw new OctopusImportProjectSelectionBeyondReadException(sessionId);
    }

    private async Task<OctopusImportSession> LoadOwnedSessionAsync(Guid sessionId, int spaceId, IReadOnlyCollection<OctopusImportSessionState> allowed, CancellationToken ct)
    {
        var ownerUserId = currentUser.Id ?? throw new UnauthorizedAccessException("Choosing Octopus projects requires an authenticated user.");
        var session = await sessionDataProvider.GetSessionNoTrackingAsync(sessionId, ownerUserId, spaceId, ct).ConfigureAwait(false) ?? throw new OctopusImportSessionNotFoundException(sessionId);
        var state = Enum.Parse<OctopusImportSessionState>(session.State, ignoreCase: true);

        if (!allowed.Contains(state))
            throw new OctopusImportSessionStateTransitionException(state, allowed.First());

        return session;
    }
}
