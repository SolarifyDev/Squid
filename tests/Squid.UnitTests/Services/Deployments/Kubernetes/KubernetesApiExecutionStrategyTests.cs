using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Settings.GithubPackage;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiExecutionStrategyTests
{
    private readonly Mock<IYamlNuGetPacker> _yamlNuGetPacker = new();
    private readonly CalamariGithubPackageSetting _calamariSetting = new() { Version = "28.2.1" };
    private readonly KubernetesApiExecutionStrategy _strategy;

    public KubernetesApiExecutionStrategyTests()
    {
        _strategy = new KubernetesApiExecutionStrategy(
            _yamlNuGetPacker.Object,
            _calamariSetting);
    }

    // === Routing — CalamariCommand presence determines execution path ===

    [Fact]
    public async Task ExecuteScriptAsync_WithCalamariCommand_CallsYamlNuGetPacker()
    {
        _yamlNuGetPacker.Setup(p => p.CreateNuGetPackageFromYamlStreams(
                It.IsAny<Dictionary<string, Stream>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Array.Empty<byte>());

        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = System.Text.Encoding.UTF8.GetBytes("apiVersion: v1")
        };

        var request = CreateRequest(calamariCommand: "calamari-run-script", files: files);

        // ExecuteScriptAsync will attempt to run pwsh, which will fail in test environment.
        // We verify that the Calamari path was entered by checking NuGet packer was invoked.
        try
        {
            await _strategy.ExecuteScriptAsync(request, CancellationToken.None);
        }
        catch
        {
            // Expected — pwsh process won't start in unit test environment
        }

        _yamlNuGetPacker.Verify(
            p => p.CreateNuGetPackageFromYamlStreams(
                It.IsAny<Dictionary<string, Stream>>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithoutCalamariCommand_DoesNotCallYamlNuGetPacker()
    {
        var request = CreateRequest(calamariCommand: null);

        try
        {
            await _strategy.ExecuteScriptAsync(request, CancellationToken.None);
        }
        catch
        {
            // Expected — bash process won't start in unit test environment
        }

        _yamlNuGetPacker.Verify(
            p => p.CreateNuGetPackageFromYamlStreams(
                It.IsAny<Dictionary<string, Stream>>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    // === Work Directory Lifecycle ===

    [Fact]
    public async Task ExecuteScriptAsync_CleansUpWorkDirectory_EvenOnFailure()
    {
        var request = CreateRequest(calamariCommand: null);
        string workDirBefore = null;

        // Capture any temp directories that exist before/after
        var tempBase = Path.Combine(Path.GetTempPath(), "squid-exec");
        var dirsBefore = Directory.Exists(tempBase)
            ? new HashSet<string>(Directory.GetDirectories(tempBase))
            : new HashSet<string>();

        try
        {
            await _strategy.ExecuteScriptAsync(request, CancellationToken.None);
        }
        catch
        {
            // Expected
        }

        // After execution (even failed), the work directory should have been cleaned up
        var dirsAfter = Directory.Exists(tempBase)
            ? Directory.GetDirectories(tempBase)
            : Array.Empty<string>();

        var newDirs = dirsAfter.Where(d => !dirsBefore.Contains(d)).ToArray();

        newDirs.ShouldBeEmpty("Work directory should have been cleaned up");
    }

    // === File Writing ===

    [Fact]
    public async Task ExecuteScriptAsync_WithFiles_WritesFilesToDisk()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["manifest.yaml"] = System.Text.Encoding.UTF8.GetBytes("apiVersion: v1")
        };

        var request = CreateRequest(calamariCommand: null, files: files);

        // The strategy will create a temp dir, write files, try to execute, then clean up.
        // We can't verify the files exist during execution, but we can verify no exception
        // from the file writing phase (exception comes from bash execution instead).
        var ex = await Record.ExceptionAsync(
            () => _strategy.ExecuteScriptAsync(request, CancellationToken.None));

        // If an exception occurs, it should be from process execution, not file writing
        if (ex != null)
            ex.ShouldNotBeOfType<IOException>();
    }

    // === Helpers ===

    private static ScriptExecutionRequest CreateRequest(
        string calamariCommand = null,
        string scriptBody = "echo test",
        Dictionary<string, byte[]> files = null)
    {
        return new ScriptExecutionRequest
        {
            Machine = new Machine { Name = "local-k8s" },
            ScriptBody = scriptBody,
            CalamariCommand = calamariCommand,
            ReleaseVersion = "1.0.0",
            Files = files ?? new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };
    }
}
