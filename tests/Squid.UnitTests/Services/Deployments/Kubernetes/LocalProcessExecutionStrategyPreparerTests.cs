using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class LocalProcessExecutionStrategyPreparerTests
{
    private readonly Mock<ICalamariPayloadBuilder> _payloadBuilder = new();
    private readonly Mock<ILocalProcessRunner> _processRunner = new();
    private readonly Mock<IScriptContextPreparer> _preparer = new();

    public LocalProcessExecutionStrategyPreparerTests()
    {
        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
    }

    // === Preparer is used when available ===

    [Fact]
    public async Task Execute_WithPreparer_UsesPreparerResult()
    {
        _preparer
            .Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptContextResult { Script = "prepared-script" });

        var strategy = new LocalProcessExecutionStrategy(_payloadBuilder.Object, _processRunner.Object, contextPreparer: _preparer.Object);

        await strategy.ExecuteScriptAsync(CreateDirectRequest(), CancellationToken.None);

        _preparer.Verify(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // === Preparer returns env vars — passed to runner ===

    [Fact]
    public async Task Execute_PreparerReturnsEnvVars_PassedToRunner()
    {
        Dictionary<string, string> capturedEnvVars = null;

        var envVars = new Dictionary<string, string> { ["KUBECONFIG"] = "/tmp/kc" };

        _preparer
            .Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptContextResult { Script = "script", EnvironmentVariables = envVars });

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<string, string, string, CancellationToken, TimeSpan?, SensitiveValueMasker, Dictionary<string, string>>((_, _, _, _, _, _, env) => capturedEnvVars = env)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        var strategy = new LocalProcessExecutionStrategy(_payloadBuilder.Object, _processRunner.Object, contextPreparer: _preparer.Object);

        await strategy.ExecuteScriptAsync(CreateDirectRequest(), CancellationToken.None);

        capturedEnvVars.ShouldNotBeNull();
        capturedEnvVars.ShouldContainKeyAndValue("KUBECONFIG", "/tmp/kc");
    }

    // === No preparer — runs as-is ===

    [Fact]
    public async Task Execute_NoPreparer_RunsAsIs()
    {
        var strategy = new LocalProcessExecutionStrategy(_payloadBuilder.Object, _processRunner.Object);

        await strategy.ExecuteScriptAsync(CreateDirectRequest(), CancellationToken.None);

        _preparer.VerifyNoOtherCalls();
    }

    // === Packaged payload — preparer not called ===

    [Fact]
    public async Task Execute_PackagedPayload_PreparerNotCalledForContextPreparation()
    {
        _payloadBuilder
            .Setup(x => x.Build(It.IsAny<ScriptExecutionRequest>(), It.IsAny<ScriptSyntax>()))
            .Returns(new CalamariPayload
            {
                PackageFileName = "squid.1.0.0.nupkg",
                PackageBytes = Array.Empty<byte>(),
                VariableBytes = Array.Empty<byte>(),
                SensitiveBytes = Array.Empty<byte>(),
                SensitivePassword = string.Empty,
                TemplateBody = "template"
            });

        var strategy = new LocalProcessExecutionStrategy(_payloadBuilder.Object, _processRunner.Object, contextPreparer: _preparer.Object);

        var request = CreateDirectRequest();
        request.ExecutionMode = ExecutionMode.PackagedPayload;

        await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        _preparer.Verify(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // === Preparer returns empty env vars — null passed to runner ===

    [Fact]
    public async Task Execute_PreparerReturnsEmptyEnvVars_NullPassedToRunner()
    {
        Dictionary<string, string> capturedEnvVars = null;

        _preparer
            .Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptContextResult { Script = "script", EnvironmentVariables = new Dictionary<string, string>() });

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<string, string, string, CancellationToken, TimeSpan?, SensitiveValueMasker, Dictionary<string, string>>((_, _, _, _, _, _, env) => capturedEnvVars = env)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        var strategy = new LocalProcessExecutionStrategy(_payloadBuilder.Object, _processRunner.Object, contextPreparer: _preparer.Object);

        await strategy.ExecuteScriptAsync(CreateDirectRequest(), CancellationToken.None);

        capturedEnvVars.ShouldBeNull();
    }

    // === Context prep skipped when policy is Skip ===

    [Fact]
    public async Task Execute_ContextPolicySkip_PreparerNotCalled()
    {
        var strategy = new LocalProcessExecutionStrategy(_payloadBuilder.Object, _processRunner.Object, contextPreparer: _preparer.Object);

        var request = CreateDirectRequest();
        request.ContextPreparationPolicy = ContextPreparationPolicy.Skip;

        await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        _preparer.Verify(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // === Helpers ===

    private static ScriptExecutionRequest CreateDirectRequest(string scriptBody = "echo test", ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        return new ScriptExecutionRequest
        {
            Machine = new Machine { Name = "local" },
            ScriptBody = scriptBody,
            ExecutionMode = ExecutionMode.DirectScript,
            Syntax = syntax,
            ReleaseVersion = "1.0.0",
            Files = new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };
    }
}
