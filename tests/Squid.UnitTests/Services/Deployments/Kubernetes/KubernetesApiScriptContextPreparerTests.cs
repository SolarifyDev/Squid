using System.IO;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiScriptContextPreparerTests : IDisposable
{
    private readonly Mock<IKubernetesApiContextScriptBuilder> _builder = new();
    private readonly Mock<ILocalProcessRunner> _processRunner = new();
    private readonly KubernetesApiScriptContextPreparer _preparer;
    private readonly string _workDir;

    public KubernetesApiScriptContextPreparerTests()
    {
        _preparer = new KubernetesApiScriptContextPreparer(_builder.Object, _processRunner.Object);
        _workDir = Path.Combine(Path.GetTempPath(), "squid-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    // === Shell syntax — delegates to WrapWithContext ===

    [Theory]
    [InlineData(ScriptSyntax.Bash)]
    [InlineData(ScriptSyntax.PowerShell)]
    public async Task PrepareAsync_ShellSyntax_DelegatesToWrapWithContext(ScriptSyntax syntax)
    {
        var context = new ScriptContext { Syntax = syntax };

        _builder.Setup(b => b.WrapWithContext("echo hi", context, null)).Returns("wrapped-script");

        var result = await _preparer.PrepareAsync("echo hi", context, _workDir, CancellationToken.None);

        result.Script.ShouldBe("wrapped-script");
        result.EnvironmentVariables.ShouldBeEmpty();
        _builder.Verify(b => b.WrapWithContext("echo hi", context, null), Times.Once);
        _processRunner.VerifyNoOtherCalls();
    }

    // === Non-shell syntax — runs setup script, returns env vars ===

    [Theory]
    [InlineData(ScriptSyntax.Python)]
    [InlineData(ScriptSyntax.CSharp)]
    [InlineData(ScriptSyntax.FSharp)]
    public async Task PrepareAsync_NonShellSyntax_RunsSetupScript_ReturnsEnvVars(ScriptSyntax syntax)
    {
        var context = new ScriptContext { Syntax = syntax };

        _builder.Setup(b => b.BuildSetupScript(context, null)).Returns("#!/bin/bash\nsetup-stuff");

        _processRunner
            .Setup(r => r.RunAsync("bash", It.IsAny<string>(), _workDir, It.IsAny<CancellationToken>(), null, null, null))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = new List<string>
                {
                    "Cluster \"squid-cluster\" set.",
                    "Context \"squid-context\" created.",
                    "SQUID_KUBECONFIG=/tmp/kubectl-config-abc123"
                }
            });

        var result = await _preparer.PrepareAsync("print('hello')", context, _workDir, CancellationToken.None);

        result.Script.ShouldBe("print('hello')");
        result.EnvironmentVariables.ShouldContainKeyAndValue("KUBECONFIG", "/tmp/kubectl-config-abc123");
        _builder.Verify(b => b.BuildSetupScript(context, null), Times.Once);
        _builder.Verify(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()), Times.Never);
    }

    // === Non-shell — script is not modified ===

    [Fact]
    public async Task PrepareAsync_NonShell_ScriptUnchanged()
    {
        var originalScript = "using System;\nConsole.WriteLine(\"test\");";
        var context = new ScriptContext { Syntax = ScriptSyntax.CSharp };

        _builder.Setup(b => b.BuildSetupScript(It.IsAny<ScriptContext>(), null)).Returns("setup");

        _processRunner
            .Setup(r => r.RunAsync("bash", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null, null, null))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                LogLines = new List<string> { "SQUID_KUBECONFIG=/tmp/kc" }
            });

        var result = await _preparer.PrepareAsync(originalScript, context, _workDir, CancellationToken.None);

        result.Script.ShouldBe(originalScript);
    }

    // === Non-shell — setup script file is written to workDir ===

    [Fact]
    public async Task PrepareAsync_NonShell_WritesSetupScriptToWorkDir()
    {
        var context = new ScriptContext { Syntax = ScriptSyntax.Python };

        _builder.Setup(b => b.BuildSetupScript(It.IsAny<ScriptContext>(), null)).Returns("#!/bin/bash\nsetup-content");

        string capturedArgs = null;

        _processRunner
            .Setup(r => r.RunAsync("bash", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null, null, null))
            .Callback<string, string, string, CancellationToken, TimeSpan?, SensitiveValueMasker, Dictionary<string, string>>((_, args, _, _, _, _, _) => capturedArgs = args)
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                LogLines = new List<string> { "SQUID_KUBECONFIG=/tmp/kc" }
            });

        await _preparer.PrepareAsync("script", context, _workDir, CancellationToken.None);

        var expectedPath = Path.Combine(_workDir, "kubectl-context-setup.sh");
        capturedArgs.ShouldContain("kubectl-context-setup.sh");
        File.Exists(expectedPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(expectedPath);
        content.ShouldBe("#!/bin/bash\nsetup-content");
    }

    // === Setup fails — throws with details ===

    [Fact]
    public async Task PrepareAsync_SetupFails_ThrowsWithDetails()
    {
        var context = new ScriptContext { Syntax = ScriptSyntax.Python };

        _builder.Setup(b => b.BuildSetupScript(It.IsAny<ScriptContext>(), null)).Returns("setup");

        _processRunner
            .Setup(r => r.RunAsync("bash", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null, null, null))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = false,
                ExitCode = 1,
                StderrLines = new List<string> { "ERROR: kubectl config set-cluster failed" }
            });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _preparer.PrepareAsync("script", context, _workDir, CancellationToken.None));

        ex.Message.ShouldContain("Kubectl context setup failed");
        ex.Message.ShouldContain("exit code 1");
        ex.Message.ShouldContain("kubectl config set-cluster failed");
    }

    // === Proxy vars included in env vars ===

    [Fact]
    public async Task PrepareAsync_ProxyVars_IncludedInEnvVars()
    {
        var context = new ScriptContext { Syntax = ScriptSyntax.Python };

        _builder.Setup(b => b.BuildSetupScript(It.IsAny<ScriptContext>(), null)).Returns("setup");

        _processRunner
            .Setup(r => r.RunAsync("bash", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null, null, null))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                LogLines = new List<string>
                {
                    "SQUID_KUBECONFIG=/tmp/kc",
                    "SQUID_HTTPS_PROXY=http://proxy:8080",
                    "SQUID_HTTP_PROXY=http://proxy:8080",
                    "SQUID_NO_PROXY=localhost,127.0.0.1"
                }
            });

        var result = await _preparer.PrepareAsync("script", context, _workDir, CancellationToken.None);

        result.EnvironmentVariables.ShouldContainKeyAndValue("KUBECONFIG", "/tmp/kc");
        result.EnvironmentVariables.ShouldContainKeyAndValue("HTTPS_PROXY", "http://proxy:8080");
        result.EnvironmentVariables.ShouldContainKeyAndValue("HTTP_PROXY", "http://proxy:8080");
        result.EnvironmentVariables.ShouldContainKeyAndValue("NO_PROXY", "localhost,127.0.0.1");
    }

    // === Azure config dir ===

    [Fact]
    public async Task PrepareAsync_AzureConfigDir_IncludedInEnvVars()
    {
        var context = new ScriptContext { Syntax = ScriptSyntax.CSharp };

        _builder.Setup(b => b.BuildSetupScript(It.IsAny<ScriptContext>(), null)).Returns("setup");

        _processRunner
            .Setup(r => r.RunAsync("bash", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null, null, null))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                LogLines = new List<string>
                {
                    "SQUID_KUBECONFIG=/tmp/kc",
                    "SQUID_AZURE_CONFIG_DIR=/tmp/azure-cli-xyz"
                }
            });

        var result = await _preparer.PrepareAsync("script", context, _workDir, CancellationToken.None);

        result.EnvironmentVariables.ShouldContainKeyAndValue("AZURE_CONFIG_DIR", "/tmp/azure-cli-xyz");
    }

    // === Null context defaults to Bash (shell) ===

    [Fact]
    public async Task PrepareAsync_NullContext_DefaultsToBash_DelegatesToWrapper()
    {
        _builder.Setup(b => b.WrapWithContext("echo hi", null, null)).Returns("wrapped");

        var result = await _preparer.PrepareAsync("echo hi", null, _workDir, CancellationToken.None);

        result.Script.ShouldBe("wrapped");
        _processRunner.VerifyNoOtherCalls();
    }

    // === ParseEnvironmentVariables unit tests ===

    [Fact]
    public void ParseEnvironmentVariables_ValidLines_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Some kubectl output",
            "SQUID_KUBECONFIG=/tmp/kc-123",
            "SQUID_HTTPS_PROXY=http://p:8080",
            "Another line"
        };

        var envVars = KubernetesApiScriptContextPreparer.ParseEnvironmentVariables(lines);

        envVars.Count.ShouldBe(2);
        envVars["KUBECONFIG"].ShouldBe("/tmp/kc-123");
        envVars["HTTPS_PROXY"].ShouldBe("http://p:8080");
    }

    [Fact]
    public void ParseEnvironmentVariables_EmptyLines_ReturnsEmpty()
    {
        var envVars = KubernetesApiScriptContextPreparer.ParseEnvironmentVariables(new List<string>());

        envVars.ShouldBeEmpty();
    }

    [Fact]
    public void ParseEnvironmentVariables_NoSquidPrefix_ReturnsEmpty()
    {
        var lines = new List<string> { "KUBECONFIG=/tmp/kc", "OTHER=value" };

        var envVars = KubernetesApiScriptContextPreparer.ParseEnvironmentVariables(lines);

        envVars.ShouldBeEmpty();
    }

    [Fact]
    public void ParseEnvironmentVariables_UnknownSquidVar_Ignored()
    {
        var lines = new List<string> { "SQUID_UNKNOWN=value", "SQUID_KUBECONFIG=/tmp/kc" };

        var envVars = KubernetesApiScriptContextPreparer.ParseEnvironmentVariables(lines);

        envVars.Count.ShouldBe(1);
        envVars.ShouldContainKey("KUBECONFIG");
    }
}
