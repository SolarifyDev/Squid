using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshScriptContextWrapperTests
{
    [Fact]
    public void WrapScript_ReturnsScriptUnmodified()
    {
        var wrapper = new SshScriptContextWrapper();
        var script = "#!/bin/bash\necho hello";
        var context = new ScriptContext();

        var result = wrapper.WrapScript(script, context);

        result.ShouldBe(script);
    }

    [Fact]
    public void WrapScript_EmptyScript_ReturnsEmpty()
    {
        var wrapper = new SshScriptContextWrapper();

        var result = wrapper.WrapScript(string.Empty, new ScriptContext());

        result.ShouldBe(string.Empty);
    }
}
