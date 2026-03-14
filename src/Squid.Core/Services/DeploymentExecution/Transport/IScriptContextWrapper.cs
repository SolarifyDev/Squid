namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IScriptContextWrapper : IScopedDependency
{
    string WrapScript(string script, ScriptContext context);
}
