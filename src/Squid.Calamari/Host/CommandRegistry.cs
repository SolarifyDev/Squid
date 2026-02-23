namespace Squid.Calamari.Host;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _lookup;

    public CommandRegistry(IEnumerable<ICommandHandler> handlers)
    {
        if (handlers is null)
            throw new ArgumentNullException(nameof(handlers));

        var orderedHandlers = handlers.ToArray();
        if (orderedHandlers.Length == 0)
            throw new ArgumentException("At least one command handler must be registered.", nameof(handlers));

        _lookup = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        Handlers = orderedHandlers;

        foreach (var handler in orderedHandlers)
        {
            Register(handler.Descriptor.Name, handler);

            foreach (var alias in handler.Descriptor.Aliases)
                Register(alias, handler);
        }
    }

    public IReadOnlyList<ICommandHandler> Handlers { get; }

    public bool TryGet(string name, out ICommandHandler? handler)
        => _lookup.TryGetValue(name, out handler);

    private void Register(string name, ICommandHandler handler)
    {
        if (_lookup.TryGetValue(name, out var existing))
        {
            throw new InvalidOperationException(
                $"Duplicate command registration for '{name}' between '{existing.Descriptor.Name}' and '{handler.Descriptor.Name}'.");
        }

        _lookup[name] = handler;
    }
}
