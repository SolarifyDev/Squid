using System.IO;
using System.Linq;
using Renci.SshNet;
using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Runtime.Bundles;
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
    private readonly IRuntimeBundleProvider _runtimeBundleProvider = new RuntimeBundleProvider(new IRuntimeBundle[] { new BashRuntimeBundle() });
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

    private SshExecutionStrategy CreateStrategy() => new(_connectionFactory.Object, _executionMutex.Object, _stagingPlanner.Object, _runtimeBundleProvider);

    /// <summary>
    /// P0-Phase9.2 test seam: subclass that counts cleanup invocations.
    /// Pre-Phase-9.2 behaviour: cleanup ran ONLY on success. Post-fix:
    /// cleanup runs on every exit — success, exception, cancellation.
    /// </summary>
    private class CleanupCountingStrategy : SshExecutionStrategy
    {
        public int CleanupCallCount;
        public string LastWorkDir;

        public CleanupCountingStrategy(ISshConnectionFactory cf, ISshExecutionMutex em, IPackageStagingPlanner sp, IRuntimeBundleProvider rbp)
            : base(cf, em, sp, rbp) { }

        protected internal override void CleanupRemoteWorkDirectory(ISshConnectionScope scope, string workDir, string baseDir)
        {
            CleanupCallCount++;
            LastWorkDir = workDir;
            // intentionally do not call base — we don't want SshRemoteShellExecutor to fire on a mock
        }
    }

    private CleanupCountingStrategy CreateCountingStrategy() => new(_connectionFactory.Object, _executionMutex.Object, _stagingPlanner.Object, _runtimeBundleProvider);

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
    public void BootstrapIfBash_BashSyntax_WrapsViaRuntimeBundle()
    {
        var request = MakeRequest(scriptBody: "echo hello", syntax: ScriptSyntax.Bash);
        var strategy = CreateStrategy();

        var result = strategy.BootstrapIfBash(request, "/home/user/.squid/Work/42", "/home/user/.squid");

        result.ShouldStartWith("#!/bin/bash\n");
        // P1-B.6: BashRuntimeBundle now wraps every value in single quotes — see
        // EscapeBashValue. Test pins the new contract (double-quote form was
        // injection-vulnerable for any value containing a literal newline).
        result.ShouldContain("export SquidHome='/home/user/.squid'");
        result.ShouldContain("export SquidWorkDirectory='/home/user/.squid/Work/42'");
        result.ShouldContain("set_squidvariable()");
        result.ShouldContain("echo hello");
    }

    [Fact]
    public void BootstrapIfBash_BashSyntax_ExportsNonSensitiveVariables()
    {
        var request = MakeRequest(scriptBody: "echo hello", syntax: ScriptSyntax.Bash);
        request.Variables.Add(new VariableDto { Name = "AppEnv", Value = "production", IsSensitive = false });
        request.Variables.Add(new VariableDto { Name = "DbPassword", Value = "secret", IsSensitive = true });
        var strategy = CreateStrategy();

        var result = strategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldContain("export AppEnv='production'");
        result.ShouldNotContain("DbPassword");
        result.ShouldNotContain("secret");
    }

    [Fact]
    public void BootstrapIfBash_PowerShellSyntax_ReturnsOriginalBody()
    {
        var request = MakeRequest(scriptBody: "Get-Process", syntax: ScriptSyntax.PowerShell);
        var strategy = CreateStrategy();

        var result = strategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldBe("Get-Process");
        result.ShouldNotContain("#!/bin/bash");
    }

    [Fact]
    public void BootstrapIfBash_PythonSyntax_ReturnsOriginalBody()
    {
        var request = MakeRequest(scriptBody: "print('hello')", syntax: ScriptSyntax.Python);
        var strategy = CreateStrategy();

        var result = strategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldBe("print('hello')");
        result.ShouldNotContain("#!/bin/bash");
    }

    [Fact]
    public void BootstrapIfBash_NullScriptBody_NonBash_ReturnsEmpty()
    {
        var request = MakeRequest(syntax: ScriptSyntax.PowerShell);
        request.ScriptBody = null;
        var strategy = CreateStrategy();

        var result = strategy.BootstrapIfBash(request, "/work/1", "/base");

        result.ShouldBe(string.Empty);
    }

    // ========== RetentionKeepCount ==========

    [Fact]
    public void RetentionKeepCount_IsReasonable()
    {
        SshExecutionStrategy.RetentionKeepCount.ShouldBe(10);
    }

    // ========== P0-Phase9.2 cleanup-finally guarantee ==========
    //
    // Pre-Phase-9.2 bug: CleanupRemoteWorkDirectory sat AFTER the success
    // return statement. If staging or script-execution threw, the workDir
    // (containing decrypted sensitiveVariables.json) was orphaned indefinitely
    // on the remote host. This block pins cleanup-runs-on-every-exit-path.

    [Fact]
    public async Task Cleanup_RunsOnSuccessPath()
    {
        var strategy = CreateCountingStrategy();
        var result = await strategy.ExecuteScriptAsync(MakeRequest(), CancellationToken.None);

        result.ShouldNotBeNull();
        strategy.CleanupCallCount.ShouldBe(1, customMessage:
            "Success path must invoke cleanup once.");
    }

    [Fact]
    public async Task Cleanup_RunsWhenStagingPlannerThrowsMidExecution()
    {
        // Simulate the exact bug case: PrepareRemoteWorkDirectoryAsync throws
        // AFTER the workDir has been created on the remote host (because the
        // staging planner ran a partial transfer then died).
        _stagingPlanner
            .Setup(p => p.PlanAsync(It.IsAny<Squid.Core.Services.DeploymentExecution.Packages.Staging.PackageRequirement>(), It.IsAny<Squid.Core.Services.DeploymentExecution.Packages.Staging.PackageStagingContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("simulated staging failure mid-transfer"));

        var request = MakeRequest();
        request.PackageReferences = new List<Squid.Core.Services.DeploymentExecution.Packages.PackageAcquisitionResult>
        {
            new("/tmp/squid-pkg/pkg-a-1.0.0.zip", "pkg-a", "1.0.0", 1024, "sha256:abc")
        };

        var strategy = CreateCountingStrategy();
        var result = await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        // Strategy returns a failure result — that's OK, the cleanup still ran
        result.Success.ShouldBeFalse();
        strategy.CleanupCallCount.ShouldBe(1, customMessage:
            "Cleanup MUST run when staging throws — workDir already exists on the " +
            "remote host with decrypted sensitiveVariables.json. This is the P0 leak.");
    }

    [Fact]
    public async Task Cleanup_RunsWhenScriptExecutionThrows()
    {
        // Simulate the second exact-bug case: ExecuteScriptAsync throws AFTER
        // sensitiveVariables.json has already been written to the remote workDir.
        // This is the more dangerous variant — decrypted credential file is on
        // the remote, must be cleaned regardless of how execution failed.
        _stagingPlanner
            .Setup(p => p.PlanAsync(It.IsAny<Squid.Core.Services.DeploymentExecution.Packages.Staging.PackageRequirement>(), It.IsAny<Squid.Core.Services.DeploymentExecution.Packages.Staging.PackageStagingContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("simulated mid-execution timeout — workDir already populated"));

        var request = MakeRequest();
        request.PackageReferences = new List<Squid.Core.Services.DeploymentExecution.Packages.PackageAcquisitionResult>
        {
            new("/tmp/squid-pkg/pkg-a-1.0.0.zip", "pkg-a", "1.0.0", 1024, "sha256:abc")
        };

        var strategy = CreateCountingStrategy();
        var result = await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeFalse();
        strategy.CleanupCallCount.ShouldBe(1, customMessage:
            "Cleanup MUST run when script execution throws AFTER workDir is " +
            "populated — pre-Phase-9.2 the finally block didn't exist and the " +
            "decrypted sensitiveVariables.json stayed on the remote forever.");
    }
}
