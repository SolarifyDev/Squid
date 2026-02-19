using System.IO.Compression;
using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Exceptions;
using Squid.Core.Services.Tentacle;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private async Task DownloadCalamariAsync(CancellationToken ct)
    {
        _ctx.CalamariPackageBytes = await DownloadCalamariPackageAsync().ConfigureAwait(false);

        Log.Information("Calamari package downloaded for deployment {DeploymentId}", _ctx.Deployment.Id);
    }

    private async Task ExtractCalamariOnAllTargetsAsync(CancellationToken ct)
    {
        var extractScript = UtilService.GetEmbeddedScriptContent("ExtractCalamariPackage.ps1");

        foreach (var tc in _ctx.AllTargetsContext)
        {
            _ctx.CurrentDeployTargetContext = tc;
            tc.CalamariPackageBytes = _ctx.CalamariPackageBytes;

            var success = await ExtractCalamariPackageOnTargetAsync(
                ct, extractScript, tc.CalamariPackageBytes, tc.Machine).ConfigureAwait(false);

            if (!success)
                throw new DeploymentScriptException(
                    $"Calamari extraction failed on {tc.Machine.Name}", _ctx.Deployment.Id);
        }

        Log.Information("Calamari package extraction completed on all targets for deployment {DeploymentId}", _ctx.Deployment.Id);
    }

    private async Task<bool> ExtractCalamariPackageOnTargetAsync(CancellationToken ct, string extractCalamariPackageScript, byte[] calamariPackageBytes, Persistence.Entities.Deployments.Machine target)
    {
        var extractExecution = await StartExtractCalamariPackageScriptAsync(
            extractCalamariPackageScript,
            calamariPackageBytes,
            target,
            ct).ConfigureAwait(false);

        var (success, _) = await ObserveDeploymentScriptAsync(extractExecution, ct).ConfigureAwait(false);
        return success;
    }

    private async Task<byte[]> DownloadCalamariPackageAsync()
    {
        const string packageId = "Calamari";
        const string githubUserName = "SolarifyDev";

        var version = _calamariGithubPackageSetting.Version;

        var cacheDirectory = string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.CacheDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "CalamariPackages")
            : _calamariGithubPackageSetting.CacheDirectory;

        Directory.CreateDirectory(cacheDirectory);

        var cacheFilePath = Path.Combine(cacheDirectory, $"Calamari.{version}.nupkg");

        if (File.Exists(cacheFilePath))
        {
            Log.Information("Using cached Calamari package from {CachePath}", cacheFilePath);
            return await File.ReadAllBytesAsync(cacheFilePath).ConfigureAwait(false);
        }

        var downloader = new GithubPackageDownloader(githubUserName, _calamariGithubPackageSetting.Token, _calamariGithubPackageSetting.MirrorUrlTemplate);
        var packageStream = await downloader.DownloadPackageAsync(packageId, version).ConfigureAwait(false);

        var bytes = ReadAllBytes(packageStream);

        await File.WriteAllBytesAsync(cacheFilePath, bytes).ConfigureAwait(false);

        Log.Information("Downloaded and cached Calamari package to {CachePath}", cacheFilePath);

        return bytes;
    }

    private async Task<(Persistence.Entities.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)> StartDeployByCalamariScriptAsync(
        string deployByCalamariScript,
        byte[] yamlNuGetPackageBytes,
        Stream variableJsonStream,
        Stream sensitiveVariableJsonStream,
        string sensitiveVariablesPassword,
        Persistence.Entities.Deployments.Machine target,
        string version,
        CancellationToken ct)
    {
        if (target == null)
            throw new DeploymentTargetException("No target machine to execute DeployByCalamari script");

        var packageBytes = yamlNuGetPackageBytes ?? Array.Empty<byte>();
        var variableBytes = ReadAllBytes(variableJsonStream);
        var sensitiveBytes = ReadAllBytes(sensitiveVariableJsonStream);

        var packageFileName = $"squid.{version}.nupkg";
        const string variableFileName = "variables.json";
        const string sensitiveVariableFileName = "sensitiveVariables.json";

        var packageFilePath = $".\\{packageFileName}";
        var variableFilePath = $".\\{variableFileName}";
        var sensitiveVariableFilePath = string.IsNullOrEmpty(sensitiveVariablesPassword) ? string.Empty : $".\\{sensitiveVariableFileName}";

        var scriptBody = deployByCalamariScript
            .Replace("{{PackageFilePath}}", packageFilePath, StringComparison.Ordinal)
            .Replace("{{VariableFilePath}}", variableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariableFile}}", sensitiveVariableFilePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariablePassword}}", sensitiveVariablesPassword, StringComparison.Ordinal)
            .Replace("{{CalamariVersion}}", GetCalamariVersion() + $"/{target.OperatingSystem.GetDescription()}", StringComparison.Ordinal);

        ct.ThrowIfCancellationRequested();

        var endpoint = ParseMachineEndpoint(target);

        if (endpoint == null)
            throw new DeploymentEndpointException(target.Name);

        var scriptFiles = new[]
        {
            new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes), null),
            new ScriptFile(variableFileName, DataStream.FromBytes(variableBytes), null),
            new ScriptFile(sensitiveVariableFileName, DataStream.FromBytes(sensitiveBytes), sensitiveVariablesPassword)
        };

        Log.Information("Starting DeployByCalamari with script {@Script}", scriptBody);

        var command = new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting DeployByCalamari script on machine {MachineName} with ticket id: {Ticket}", target.Name, ticket);

        return (target, scriptClient, ticket);
    }

    private async Task<(Persistence.Entities.Deployments.Machine Machine, IAsyncScriptService ScriptClient, ScriptTicket Ticket)> StartExtractCalamariPackageScriptAsync(
        string extractCalamariPackageScript,
        byte[] calamariPackageBytes,
        Persistence.Entities.Deployments.Machine target,
        CancellationToken ct)
    {
        if (target == null)
            throw new DeploymentTargetException("No target machine to execute ExtractCalamariPackage script");

        var calamariVersion = _calamariGithubPackageSetting.Version;

        var scriptBody = extractCalamariPackageScript
            .Replace("{{CalamariPath}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{CalamariPackageVersion}}", calamariVersion, StringComparison.Ordinal)
            .Replace("{{CalamariPackage}}", "SolarifyDev.SquidCalamari", StringComparison.Ordinal)
            .Replace("{{SupportPackage}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{SupportPackageVersion}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{UsesCustomPackageDirectory}}", "false", StringComparison.Ordinal)
            .Replace("{{CalamariPackageVersionsToKeep}}", calamariVersion, StringComparison.Ordinal);

        var calamariPackageFileName = $"SolarifyDev.SquidCalamari.{calamariVersion}.nupkg";

        ct.ThrowIfCancellationRequested();

        var endpoint = ParseMachineEndpoint(target);

        if (endpoint == null)
            throw new DeploymentEndpointException(target.Name);

        var scriptFiles = new[]
        {
            new ScriptFile(calamariPackageFileName, DataStream.FromBytes(calamariPackageBytes), null)
        };

        var command = new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            TimeSpan.FromMinutes(30),
            null,
            Array.Empty<string>(),
            null,
            scriptFiles);

        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        var ticket = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

        Log.Information("Starting ExtractCalamariPackage script on machine {MachineName} with ticket id: {Ticket}", target.Name, ticket);

        return (target, scriptClient, ticket);
    }

    private ServiceEndPoint? ParseMachineEndpoint(Persistence.Entities.Deployments.Machine machine)
    {
        try
        {
            if (string.IsNullOrEmpty(machine.Uri) || string.IsNullOrEmpty(machine.Thumbprint))
            {
                Log.Warning("Machine {MachineName} has missing Uri or Thumbprint", machine.Name);
                return null;
            }

            return new ServiceEndPoint(machine.Uri, machine.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse machine endpoint for machine {MachineName}", machine.Name);
            return null;
        }
    }

    private byte[] CreateYamlNuGetPackage(Dictionary<string, Stream> yamlStreams)
    {
        if (yamlStreams == null || yamlStreams.Count == 0)
        {
            Log.Information("No YAML streams to pack into NuGet package");
            return Array.Empty<byte>();
        }

        return _yamlNuGetPacker.CreateNuGetPackageFromYamlStreams(yamlStreams);
    }

    private void CheckNugetPackage(byte[] packageBytes)
    {
        if (packageBytes == null || packageBytes.Length == 0) return;

        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Log.Information("=== NuGet Package Contents ===");
        Log.Information("Total entries in package: {Count}", archive.Entries.Count);

        foreach (var entry in archive.Entries)
        {
            Log.Information("Entry: {EntryName}, Size: {Size} bytes", entry.FullName, entry.Length);

            if (entry.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream);
                var content = reader.ReadToEnd();
                Log.Information("=== {FileName} ===\n{Content}", entry.FullName, content);
            }
        }

        Log.Information("=== End of Package Contents ===");
    }

    private string GetCalamariVersion()
    {
        return string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.Version) ? "28.2.1" : _calamariGithubPackageSetting.Version;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream == null) return Array.Empty<byte>();

        if (stream.CanSeek) stream.Position = 0;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
