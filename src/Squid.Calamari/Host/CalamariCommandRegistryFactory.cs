namespace Squid.Calamari.Host;

public static class CalamariCommandRegistryFactory
{
    public static CommandRegistry CreateDefault()
    {
        return CreateFromModules(CreateDefaultModules());
    }

    public static IReadOnlyList<ICommandModule> CreateDefaultModules()
        => [new CoreCommandModule()];

    public static CommandRegistry CreateFromModules(IEnumerable<ICommandModule> modules)
    {
        if (modules is null)
            throw new ArgumentNullException(nameof(modules));

        var handlers = modules.SelectMany(static m => m.CreateHandlers()).ToArray();
        return new CommandRegistry(handlers);
    }
}
