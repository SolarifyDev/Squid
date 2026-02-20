namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class DeploymentEntityNotFoundException : InvalidOperationException
{
    public string EntityType { get; }
    public object EntityId { get; }

    public DeploymentEntityNotFoundException(string entityType, object entityId) : base($"{entityType} {entityId} not found")
    {
        EntityType = entityType;
        EntityId = entityId;
    }

    public DeploymentEntityNotFoundException(string entityType, object entityId, string detail) : base($"{entityType} {entityId} not found. {detail}")
    {
        EntityType = entityType;
        EntityId = entityId;
    }
}
