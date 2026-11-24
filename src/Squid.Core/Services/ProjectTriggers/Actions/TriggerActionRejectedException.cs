namespace Squid.Core.Services.ProjectTriggers.Actions;

/// <summary>An operator-facing rejection. Messages are fixed descriptions and may be persisted in Run history.</summary>
public sealed class TriggerActionRejectedException : InvalidOperationException
{
    internal TriggerActionRejectedException(string message) : base(message)
    {
    }
}
