using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawScriptContextWrapper : IScriptContextWrapper
{
    public string WrapScript(string script, ScriptContext context) => script;
}
