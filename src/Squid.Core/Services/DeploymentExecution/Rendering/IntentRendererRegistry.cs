using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Rendering;

/// <summary>
/// Resolves the <see cref="IIntentRenderer"/> for a given transport + intent pair.
/// Registered renderers are supplied via DI (any <see cref="IIntentRenderer"/>
/// implementing <see cref="IScopedDependency"/> is auto-discovered).
/// </summary>
public interface IIntentRendererRegistry : IScopedDependency
{
    /// <summary>
    /// Returns the first renderer registered for <paramref name="communicationStyle"/>
    /// whose <c>CanRender(intent)</c> is <c>true</c>. Throws
    /// <see cref="UnsupportedIntentException"/> if no such renderer exists.
    /// </summary>
    IIntentRenderer Resolve(CommunicationStyle communicationStyle, ExecutionIntent intent);

    /// <summary>
    /// Returns the first renderer registered for <paramref name="communicationStyle"/>
    /// whose <c>CanRender(intent)</c> is <c>true</c>, or <c>null</c> if none exists.
    /// </summary>
    IIntentRenderer? TryResolve(CommunicationStyle communicationStyle, ExecutionIntent intent);
}

public sealed class IntentRendererRegistry : IIntentRendererRegistry
{
    private readonly IReadOnlyDictionary<CommunicationStyle, IReadOnlyList<IIntentRenderer>> _byStyle;

    public IntentRendererRegistry(IEnumerable<IIntentRenderer> renderers)
    {
        _byStyle = renderers
            .GroupBy(r => r.CommunicationStyle)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IIntentRenderer>)g.ToList());
    }

    public IIntentRenderer Resolve(CommunicationStyle communicationStyle, ExecutionIntent intent)
    {
        var resolved = TryResolve(communicationStyle, intent);

        if (resolved is null)
            throw new UnsupportedIntentException(communicationStyle, intent);

        return resolved;
    }

    public IIntentRenderer? TryResolve(CommunicationStyle communicationStyle, ExecutionIntent intent)
    {
        if (!_byStyle.TryGetValue(communicationStyle, out var candidates))
            return null;

        foreach (var renderer in candidates)
        {
            if (renderer.CanRender(intent))
                return renderer;
        }

        return null;
    }
}
