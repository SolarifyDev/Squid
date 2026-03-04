namespace Squid.Calamari.Host;

public sealed class CoreCommandModule : ICommandModule
{
    public IEnumerable<ICommandHandler> CreateHandlers()
    {
        yield return new RunScriptCliCommandHandler();
        yield return new ApplyYamlCliCommandHandler();
    }
}
