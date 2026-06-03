using System.Text.Json;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Identity;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Events;

namespace Squid.Core.Services.Events;

/// <summary>
/// Builds an <see cref="Event"/> row from a <see cref="RecordEventRequest"/>, resolving
/// provenance from the ambient <see cref="ICurrentUser"/>. Shared by the two central
/// emission points — <c>EventService.RecordAsync</c> and the SaveChanges document-audit
/// path in <c>SquidDbContext</c> — so provenance + reference serialization stay identical
/// and in one place. Pure and stateless.
/// </summary>
public static class EventFactory
{
    public static Event Build(RecordEventRequest request, ICurrentUser currentUser) => new()
    {
        Category = request.Category,
        ReferencesJson = Serialize(request.References),
        SpaceId = request.SpaceId,
        ProjectId = request.ProjectId,
        ReleaseId = request.ReleaseId,
        DeploymentId = request.DeploymentId,
        EnvironmentId = request.EnvironmentId,
        MachineId = request.MachineId,
        ServerTaskId = request.ServerTaskId,
        UserId = ResolveUserId(currentUser),
        Username = ResolveUsername(currentUser),
        // established-with + user-agent need the HTTP auth scheme / request headers;
        // PR-4 (provenance polish) resolves those via a dedicated resolver. A real
        // portal/API actor is still attributed via ICurrentUser (ResolveUserId/Name).
        EstablishedWith = currentUser is { IsInternal: false } ? EventIdentityEstablishedWith.SessionCookie : EventIdentityEstablishedWith.Server,
        UserAgent = null,
        Occurred = DateTimeOffset.UtcNow
    };

    public static string Serialize(object references) => references is null ? "{}" : JsonSerializer.Serialize(references);

    private static int? ResolveUserId(ICurrentUser currentUser) => currentUser is { IsInternal: false } ? currentUser.Id : null;

    private static string ResolveUsername(ICurrentUser currentUser) =>
        currentUser is null || currentUser.IsInternal || string.IsNullOrWhiteSpace(currentUser.Name) ? "system" : currentUser.Name;
}
