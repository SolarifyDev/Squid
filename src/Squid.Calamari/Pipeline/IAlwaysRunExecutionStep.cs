namespace Squid.Calamari.Pipeline;

/// <summary>
/// Marker interface for steps that must run in a finally-like phase.
/// </summary>
public interface IAlwaysRunExecutionStep<TContext> : IExecutionStep<TContext>
{
}
