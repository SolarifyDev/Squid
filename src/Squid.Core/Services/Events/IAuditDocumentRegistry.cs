using Squid.Core.Persistence.Entities.Deployments;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.Events;

/// <summary>
/// Single source of truth for which persisted entities are user-facing "documents" worth
/// auditing, and how to derive each one's audit identity (space + feed FKs + display name).
///
/// <para>Decoupled from the entities themselves (matching Squid's external-contributor
/// philosophy — entities carry no audit concern). The SaveChanges document-audit path in
/// <c>SquidDbContext</c> consults this registry: an entity type that is NOT registered is
/// simply not audited. Adding a new audited document = one entry here; no other change.</para>
/// </summary>
public interface IAuditDocumentRegistry : ISingletonDependency
{
    /// <summary>
    /// True when <paramref name="entity"/> is an audited document type, yielding its
    /// display type name and audit keys; false otherwise (the entity is skipped).
    /// </summary>
    bool TryDescribe(object entity, out string documentType, out AuditDocumentKeys keys);
}

public sealed class AuditDocumentRegistry : IAuditDocumentRegistry
{
    private sealed record Mapping(string DocumentType, Func<object, AuditDocumentKeys> Extract);

    // One glanceable table of every audited document. The extractor maps the entity to the
    // first-class Event FK columns so the event surfaces under that document's feed
    // (a Release event under both the release AND its project, etc.).
    private static readonly Dictionary<Type, Mapping> Mappings = new()
    {
        [typeof(Project)] = new("Project", e => { var p = (Project)e; return new AuditDocumentKeys { SpaceId = p.SpaceId, ProjectId = p.Id, Name = p.Name }; }),
        [typeof(Release)] = new("Release", e => { var r = (Release)e; return new AuditDocumentKeys { SpaceId = r.SpaceId, ProjectId = r.ProjectId, ReleaseId = r.Id, Name = r.Version }; }),
        [typeof(Environment)] = new("Environment", e => { var x = (Environment)e; return new AuditDocumentKeys { SpaceId = x.SpaceId, EnvironmentId = x.Id, Name = x.Name }; }),
        [typeof(Machine)] = new("Machine", e => { var m = (Machine)e; return new AuditDocumentKeys { SpaceId = m.SpaceId, MachineId = m.Id, Name = m.Name }; }),
        [typeof(DeploymentAccount)] = new("DeploymentAccount", e => { var a = (DeploymentAccount)e; return new AuditDocumentKeys { SpaceId = a.SpaceId, Name = a.Name }; }),
        [typeof(Channel)] = new("Channel", e => { var c = (Channel)e; return new AuditDocumentKeys { SpaceId = c.SpaceId, ProjectId = c.ProjectId, Name = c.Name }; }),
        [typeof(VariableSet)] = new("VariableSet", e => { var v = (VariableSet)e; return new AuditDocumentKeys { SpaceId = v.SpaceId, Name = v.Name }; }),
        [typeof(ProjectGroup)] = new("ProjectGroup", e => { var g = (ProjectGroup)e; return new AuditDocumentKeys { SpaceId = g.SpaceId, Name = g.Name }; }),
        [typeof(ExternalFeed)] = new("ExternalFeed", e => { var f = (ExternalFeed)e; return new AuditDocumentKeys { SpaceId = f.SpaceId, Name = f.Name }; }),
        [typeof(Certificate)] = new("Certificate", e => { var c = (Certificate)e; return new AuditDocumentKeys { SpaceId = c.SpaceId, Name = c.Name }; })
    };

    public bool TryDescribe(object entity, out string documentType, out AuditDocumentKeys keys)
    {
        documentType = null;
        keys = null;

        if (entity is null || !Mappings.TryGetValue(entity.GetType(), out var mapping)) return false;

        documentType = mapping.DocumentType;
        keys = mapping.Extract(entity);

        return true;
    }

    /// <summary>The registered document type names — exposed for drift/coverage tests.</summary>
    public static IReadOnlyCollection<string> RegisteredDocumentTypes => Mappings.Values.Select(m => m.DocumentType).ToList();
}
