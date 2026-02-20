using System.IO.Compression;
using Autofac;
using Autofac.Core;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Settings.GithubPackage;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Deployments;

public class DeploymentPipelineFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingExecutionStrategy ExecutionCapture { get; } = new();

    private string _calamariCacheDir;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        builder.RegisterType<DeploymentTaskExecutor>()
            .As<IDeploymentTaskExecutor>()
            .WithParameter(new ResolvedParameter(
                (pi, _) => pi.ParameterType == typeof(IEnumerable<IExecutionStrategy>),
                (_, _) => new IExecutionStrategy[] { ExecutionCapture }))
            .InstancePerLifetimeScope();

        _calamariCacheDir = Path.Combine(Path.GetTempPath(), $"squid-e2e-calamari-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_calamariCacheDir);

        configuration["CalamariGithubPackage:Version"] = "0.0.1-test";
        configuration["CalamariGithubPackage:CacheDirectory"] = _calamariCacheDir;

        builder.RegisterInstance(new CalamariGithubPackageSetting
        {
            Version = "0.0.1-test",
            CacheDirectory = _calamariCacheDir,
            Token = string.Empty,
            MirrorUrlTemplate = string.Empty
        }).AsSelf().SingleInstance();
    }

    protected override Task OnInitializedAsync()
    {
        CreateDummyCalamariPackage(_calamariCacheDir, "0.0.1-test");

        return Task.CompletedTask;
    }

    protected override Task OnDisposingAsync()
    {
        if (Directory.Exists(_calamariCacheDir))
            Directory.Delete(_calamariCacheDir, recursive: true);

        return Task.CompletedTask;
    }

    private static void CreateDummyCalamariPackage(string cacheDir, string version)
    {
        var packagePath = Path.Combine(cacheDir, $"Calamari.{version}.nupkg");

        using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var nuspecEntry = archive.CreateEntry("Calamari.nuspec");
        using var writer = new StreamWriter(nuspecEntry.Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package>
              <metadata>
                <id>Calamari</id>
                <version>{version}</version>
                <description>Dummy Calamari for E2E tests</description>
              </metadata>
            </package>
            """);
    }
}
