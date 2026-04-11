using System.Linq;
using Renci.SshNet;
using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshExecutionStrategyTests
{
    private readonly Mock<ISshConnectionFactory> _connectionFactory = new();
    private readonly Mock<ISshExecutionMutex> _executionMutex = new();
    private readonly Mock<IPackageStagingPlanner> _stagingPlanner = new();
    private readonly Mock<ISshConnectionScope> _scope = new();
    private readonly Mock<SshClient> _sshClient;
    private readonly Mock<SftpClient> _sftpClient;

    public SshExecutionStrategyTests()
    {
        _sshClient = new Mock<SshClient>("localhost", "user", "pass") { CallBase = false };
        _sftpClient = new Mock<SftpClient>("localhost", "user", "pass") { CallBase = false };

        _scope.Setup(s => s.GetSshClient()).Returns(_sshClient.Object);
        _scope.Setup(s => s.GetSftpClient()).Returns(_sftpClient.Object);
        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>())).Returns(_scope.Object);
        _executionMutex.Setup(m => m.AcquireAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
    }

    private SshExecutionStrategy CreateStrategy() => new(_connectionFactory.Object, _executionMutex.Object, _stagingPlanner.Object);

    private static ScriptExecutionRequest MakeRequest(string scriptBody = "echo hello", int serverTaskId = 42, ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        return new ScriptExecutionRequest
        {
            ScriptBody = scriptBody,
            ServerTaskId = serverTaskId,
            Syntax = syntax,
            Variables = new List<VariableDto>
            {
                new() { Name = SpecialVariables.Ssh.Host, Value = "ssh.example.com" },
                new() { Name = SpecialVariables.Ssh.Port, Value = "22" },
                new() { Name = SpecialVariables.Ssh.Fingerprint, Value = "abc123" },
                new() { Name = SpecialVariables.Ssh.RemoteWorkingDirectory, Value = "" },
                new() { Name = SpecialVariables.Account.Username, Value = "deploy" },
                new() { Name = SpecialVariables.Account.SshPrivateKeyFile, Value = "key-data" },
                new() { Name = SpecialVariables.Account.SshPassphrase, Value = "" },
                new() { Name = SpecialVariables.Account.Password, Value = "" }
            },
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    // ========== Connection ==========

    [Fact]
    public async Task Execute_CreatesConnectionScope()
    {
        var strategy = CreateStrategy();

        await strategy.ExecuteScriptAsync(MakeRequest(), CancellationToken.None);

        _connectionFactory.Verify(f => f.CreateScope(It.Is<SshConnectionInfo>(i => i.Host == "ssh.example.com" && i.Port == 22 && i.Username == "deploy")), Times.Once);
    }

    [Fact]
    public async Task Execute_ConnectionFailed_ReturnsFailure()
    {
        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>())).Throws(new InvalidOperationException("Connection refused"));

        var strategy = CreateStrategy();
        var result = await strategy.ExecuteScriptAsync(MakeRequest(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.LogLines.ShouldContain(l => l.Contains("Connection refused"));
    }

    [Fact]
    public async Task Execute_DisposesScope()
    {
        var strategy = CreateStrategy();

        await strategy.ExecuteScriptAsync(MakeRequest(), CancellationToken.None);

        _scope.Verify(s => s.Dispose(), Times.Once);
    }

    // ========== Remote Working Directory ==========

    [Fact]
    public async Task Execute_CustomRemoteWorkDir_UsesCustomPath()
    {
        var request = MakeRequest();
        request.Variables.First(v => v.Name == SpecialVariables.Ssh.RemoteWorkingDirectory).Value = "/opt/deploy";

        var strategy = CreateStrategy();
        await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        _connectionFactory.Verify(f => f.CreateScope(It.IsAny<SshConnectionInfo>()), Times.Once);
    }

    // ========== Relative Path Validation ==========

    [Theory]
    [InlineData("script.sh")]
    [InlineData("deploy.yaml")]
    [InlineData("values-prod.yaml")]
    [InlineData("config.json")]
    [InlineData("content/values.yaml")]
    [InlineData("bin/helpers/set_var.sh")]
    [InlineData("a/b/c/d/e/file.txt")]
    public void ValidateRelativePath_ValidPaths_NoException(string relativePath)
    {
        Should.NotThrow(() => SshExecutionStrategy.ValidateRelativePath(relativePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" ")]
    public void ValidateRelativePath_EmptyOrWhitespace_ThrowsArgumentException(string relativePath)
    {
        Should.Throw<ArgumentException>(() => SshExecutionStrategy.ValidateRelativePath(relativePath));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("foo\\bar")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:/Users/foo")]
    [InlineData("content/../secrets")]
    public void ValidateRelativePath_InvalidPaths_ThrowsArgumentException(string relativePath)
    {
        Should.Throw<ArgumentException>(() => SshExecutionStrategy.ValidateRelativePath(relativePath));
    }

    // ========== Port Parsing ==========

    [Fact]
    public async Task Execute_CustomPort_BuildsCorrectConnectionInfo()
    {
        var request = MakeRequest();
        request.Variables.First(v => v.Name == SpecialVariables.Ssh.Port).Value = "2222";

        var strategy = CreateStrategy();
        await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        _connectionFactory.Verify(f => f.CreateScope(It.Is<SshConnectionInfo>(i => i.Port == 2222)), Times.Once);
    }

    [Fact]
    public async Task Execute_InvalidPort_DefaultsTo22()
    {
        var request = MakeRequest();
        request.Variables.First(v => v.Name == SpecialVariables.Ssh.Port).Value = "not-a-number";

        var strategy = CreateStrategy();
        await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        _connectionFactory.Verify(f => f.CreateScope(It.Is<SshConnectionInfo>(i => i.Port == 22)), Times.Once);
    }

    // ========== Timeout ==========

    [Fact]
    public async Task Execute_NullTimeout_DoesNotThrow()
    {
        var request = MakeRequest();
        request.Timeout = null;

        var strategy = CreateStrategy();
        var result = await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.ShouldNotBeNull();
        _connectionFactory.Verify(f => f.CreateScope(It.IsAny<SshConnectionInfo>()), Times.Once);
    }

    // ========== Cancellation ==========

    [Fact]
    public async Task Execute_CancellationRequested_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>()))
            .Throws(new OperationCanceledException(cts.Token));

        var strategy = CreateStrategy();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            strategy.ExecuteScriptAsync(MakeRequest(), cts.Token));
    }

    // ========== Exit Code ==========

    [Fact]
    public async Task Execute_GeneralException_ReturnsNegativeOneExitCode()
    {
        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>()))
            .Throws(new InvalidOperationException("fail"));

        var strategy = CreateStrategy();
        var result = await strategy.ExecuteScriptAsync(MakeRequest(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(-1);
    }

    // ========== BootstrapIfBash ==========

    [Fact]
    public void BootstrapIfBash_BashSyntax_WrapsWithBootstrap()
    {
        var request = MakeRequest(scriptBody: "echo hello", syntax: ScriptSyntax.Bash);

        var result = SshExecutionStrategy.BootstrapIfBash(request, "/home/user/.squid/Work/42", "/home/user/.squid");

        result.ShouldStartWith("#!/bin/bash\n");
        result.ShouldContain("export SquidHome=");
        result.ShouldContain("echo hello");
    }

    [Fact]
    public void BootstrapIfBash_PowerShellSyntax_ReturnsOriginalBody()
    {
        var request = MakeRequest(scriptBody: "Get-Process", syntax: ScriptSyntax.PowerShell);

        var result = SshExecutionStrategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldBe("Get-Process");
        result.ShouldNotContain("#!/bin/bash");
    }

    [Fact]
    public void BootstrapIfBash_PythonSyntax_ReturnsOriginalBody()
    {
        var request = MakeRequest(scriptBody: "print('hello')", syntax: ScriptSyntax.Python);

        var result = SshExecutionStrategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldBe("print('hello')");
        result.ShouldNotContain("#!/bin/bash");
    }

    [Fact]
    public void BootstrapIfBash_NullScriptBody_ReturnsEmpty()
    {
        var request = MakeRequest(syntax: ScriptSyntax.PowerShell);
        request.ScriptBody = null;

        var result = SshExecutionStrategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldBe(string.Empty);
    }

    // ========== RetentionKeepCount ==========

    [Fact]
    public void RetentionKeepCount_IsReasonable()
    {
        SshExecutionStrategy.RetentionKeepCount.ShouldBe(10);
    }
}
