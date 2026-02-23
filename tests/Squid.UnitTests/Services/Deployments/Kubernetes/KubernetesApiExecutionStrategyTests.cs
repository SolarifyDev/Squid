using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiExecutionStrategyTests
{
    private readonly Mock<IYamlNuGetPacker> _yamlNuGetPacker = new();
    private readonly Mock<ILocalProcessRunner> _processRunner = new();
    private readonly Mock<ICalamariPayloadBuilder> _payloadBuilder = new();
    private readonly KubernetesApiExecutionStrategy _strategy;

    public KubernetesApiExecutionStrategyTests()
    {
        _strategy = new KubernetesApiExecutionStrategy(_payloadBuilder.Object, _processRunner.Object);

        _payloadBuilder
            .Setup(x => x.Build(It.IsAny<ScriptExecutionRequest>()))
            .Returns<ScriptExecutionRequest>(CreatePayloadForRequest);

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
    }

    // === Routing — executable selection ===

    [Fact]
    public async Task ExecuteScriptAsync_DirectScript_RunsBash()
    {
        string capturedExecutable = null;

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((exe, _, _, _) => capturedExecutable = exe)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        await _strategy.ExecuteScriptAsync(CreateRequest(calamariCommand: null), CancellationToken.None);

        capturedExecutable.ShouldBe("bash");
    }

    [Fact]
    public async Task ExecuteScriptAsync_CalamariCommand_RunsPwsh()
    {
        string capturedExecutable = null;

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((exe, _, _, _) => capturedExecutable = exe)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        await _strategy.ExecuteScriptAsync(CreateRequest(calamariCommand: "calamari-run-script"), CancellationToken.None);

        capturedExecutable.ShouldBe("pwsh");
    }

    // === Work Directory Lifecycle ===

    [Fact]
    public async Task ExecuteScriptAsync_CleansUpWorkDirectory_EvenOnFailure()
    {
        string capturedWorkDir = null;

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, wd, _) => capturedWorkDir = wd)
            .ThrowsAsync(new Exception("process failed"));

        try
        {
            await _strategy.ExecuteScriptAsync(CreateRequest(), CancellationToken.None);
        }
        catch
        {
            // Expected
        }

        capturedWorkDir.ShouldNotBeNull();
        Directory.Exists(capturedWorkDir).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteScriptAsync_CleansUpWorkDirectory_OnSuccess()
    {
        string capturedWorkDir = null;

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, wd, _) => capturedWorkDir = wd)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        await _strategy.ExecuteScriptAsync(CreateRequest(), CancellationToken.None);

        capturedWorkDir.ShouldNotBeNull();
        Directory.Exists(capturedWorkDir).ShouldBeFalse();
    }

    // === YamlNuGetPackage ===

    [Fact]
    public async Task ExecuteScriptAsync_WithCalamariCommand_CallsYamlNuGetPacker()
    {
        var packerCalled = false;
        var realBuilder = new CalamariPayloadBuilder(_yamlNuGetPacker.Object);
        _payloadBuilder.Setup(x => x.Build(It.IsAny<ScriptExecutionRequest>()))
            .Returns<ScriptExecutionRequest>(req =>
            {
                packerCalled = true;
                return realBuilder.Build(req);
            });

        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = System.Text.Encoding.UTF8.GetBytes("apiVersion: v1")
        };

        await _strategy.ExecuteScriptAsync(
            CreateRequest(calamariCommand: "calamari-run-script", files: files), CancellationToken.None);

        packerCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithoutCalamariCommand_DoesNotCallYamlNuGetPacker()
    {
        await _strategy.ExecuteScriptAsync(CreateRequest(calamariCommand: null), CancellationToken.None);

        _payloadBuilder.Verify(x => x.Build(It.IsAny<ScriptExecutionRequest>()), Times.Never);
    }

    // === Process runner receives workDir in squid-exec temp path ===

    [Fact]
    public async Task ExecuteScriptAsync_PassesWorkDirInTempPath()
    {
        string capturedWorkDir = null;

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, wd, _) => capturedWorkDir = wd)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        await _strategy.ExecuteScriptAsync(CreateRequest(), CancellationToken.None);

        capturedWorkDir.ShouldContain("squid-exec");
    }

    [Fact]
    public async Task ExecuteScriptAsync_CalamariCommand_PassesExpandedScriptWithResolvedPaths()
    {
        string capturedExecutable = null;
        string capturedArguments = null;
        string capturedWorkDir = null;

        _payloadBuilder.Setup(x => x.Build(It.IsAny<ScriptExecutionRequest>()))
            .Returns(new CalamariPayload
            {
                PackageFileName = "squid.1.0.0.nupkg",
                PackageBytes = Array.Empty<byte>(),
                VariableBytes = Array.Empty<byte>(),
                SensitiveBytes = Array.Empty<byte>(),
                SensitivePassword = string.Empty,
                TemplateBody = "pkg={{PackageFilePath}};var={{VariableFilePath}};sens={{SensitiveVariableFile}}"
            });

        _processRunner
            .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((exe, args, wd, _) =>
            {
                capturedExecutable = exe;
                capturedArguments = args;
                capturedWorkDir = wd;
            })
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        await _strategy.ExecuteScriptAsync(CreateRequest(calamariCommand: "calamari-run-script"), CancellationToken.None);

        capturedExecutable.ShouldBe("pwsh");
        capturedArguments.ShouldNotContain("{{PackageFilePath}}");
        capturedArguments.ShouldContain("squid.1.0.0.nupkg");
        capturedArguments.ShouldContain("variables.json");
        capturedArguments.ShouldContain(capturedWorkDir);
    }

    // === Helpers ===

    private static ScriptExecutionRequest CreateRequest(
        string scriptBody = "echo test",
        string calamariCommand = null,
        Dictionary<string, byte[]> files = null)
    {
        return new ScriptExecutionRequest
        {
            Machine = new Machine { Name = "local" },
            ScriptBody = scriptBody,
            CalamariCommand = calamariCommand,
            ReleaseVersion = "1.0.0",
            Files = files ?? new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };
    }

    private static CalamariPayload CreatePayloadForRequest(ScriptExecutionRequest request)
    {
        return new CalamariPayload
        {
            PackageFileName = $"squid.{request.ReleaseVersion}.nupkg",
            PackageBytes = Array.Empty<byte>(),
            VariableBytes = System.Text.Encoding.UTF8.GetBytes("{}"),
            SensitiveBytes = System.Text.Encoding.UTF8.GetBytes("{}"),
            SensitivePassword = string.Empty,
            TemplateBody = "pkg={{PackageFilePath}} var={{VariableFilePath}} sens={{SensitiveVariableFile}}"
        };
    }
}
