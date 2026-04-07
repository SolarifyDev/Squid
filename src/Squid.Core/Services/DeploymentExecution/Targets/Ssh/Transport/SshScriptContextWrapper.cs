using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshScriptContextWrapper : IScriptContextWrapper
{
    public string WrapScript(string script, ScriptContext context) => script;
}
