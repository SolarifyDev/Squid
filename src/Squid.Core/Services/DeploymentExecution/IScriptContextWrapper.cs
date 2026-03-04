namespace Squid.Core.Services.DeploymentExecution;

public interface IScriptContextWrapper : IScopedDependency
{
    string WrapScript(string script, ScriptContext context);
}
