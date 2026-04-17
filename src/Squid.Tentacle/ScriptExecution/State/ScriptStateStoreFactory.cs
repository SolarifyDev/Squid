namespace Squid.Tentacle.ScriptExecution.State;

public interface IScriptStateStoreFactory
{
    IScriptStateStore Create(string workspace);
}

public sealed class ScriptStateStoreFactory : IScriptStateStoreFactory
{
    public IScriptStateStore Create(string workspace) => new ScriptStateStore(workspace);
}
