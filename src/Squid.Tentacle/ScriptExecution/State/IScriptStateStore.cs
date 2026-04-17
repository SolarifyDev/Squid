namespace Squid.Tentacle.ScriptExecution.State;

public interface IScriptStateStore
{
    string WorkspacePath { get; }

    bool Exists();

    ScriptState Load();

    void Save(ScriptState state);

    void Delete();
}
