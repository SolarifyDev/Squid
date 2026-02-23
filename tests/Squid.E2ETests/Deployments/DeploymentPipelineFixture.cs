using System.IO.Compression;
using Autofac;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Settings.GithubPackage;
using Squid.Message.Enums;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Deployments;

public class DeploymentPipelineFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingExecutionStrategy ExecutionCapture { get; } = new();

    private string _calamariCacheDir;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        builder.Register(ctx =>
            new CapturingTransportRegistry(
                ctx.Resolve<IEnumerable<IDeploymentTransport>>(),
                ExecutionCapture))
            .As<ITransportRegistry>()
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

    // Wraps the real registry so each resolved transport uses the capturing strategy
    // while retaining real variable contribution and script wrapping.
    private sealed class CapturingTransportRegistry : ITransportRegistry
    {
        private readonly Dictionary<CommunicationStyle, IDeploymentTransport> _transports;
        private readonly CapturingExecutionStrategy _capture;

        public CapturingTransportRegistry(
            IEnumerable<IDeploymentTransport> transports,
            CapturingExecutionStrategy capture)
        {
            _transports = transports.ToDictionary(t => t.CommunicationStyle);
            _capture = capture;
        }

        public IDeploymentTransport Resolve(CommunicationStyle style)
            => _transports.TryGetValue(style, out var t)
                ? new StrategyCapturingTransport(t, _capture)
                : null;
    }

    private sealed class StrategyCapturingTransport : IDeploymentTransport
    {
        private readonly IDeploymentTransport _inner;

        public CommunicationStyle CommunicationStyle => _inner.CommunicationStyle;
        public IEndpointVariableContributor Variables => _inner.Variables;
        public IScriptContextWrapper ScriptWrapper => _inner.ScriptWrapper;
        public IExecutionStrategy Strategy { get; }

        public StrategyCapturingTransport(IDeploymentTransport inner, IExecutionStrategy strategy)
        {
            _inner = inner;
            Strategy = strategy;
        }
    }
}
