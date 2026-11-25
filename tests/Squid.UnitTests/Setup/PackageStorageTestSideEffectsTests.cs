using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Settings.Setup;
using Squid.Message.Models.PackageStorage;
using Microsoft.Extensions.Configuration;

namespace Squid.UnitTests.Setup;

/// <summary>A storage test must leave the disk as it found it, and carry the provider's warnings about the tested settings.</summary>
public sealed class PackageStorageTestSideEffectsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"squid-pkg-side-{Guid.NewGuid():N}");

    public PackageStorageTestSideEffectsTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch (IOException) { }
    }

    private static FileSystemPackageBlobStoreProvider Provider() => new(new DataDirectorySetting(new ConfigurationBuilder().Build()));

    private static PackageBlobStoreConfiguration Config(string root) => new() { Properties = new Dictionary<string, string> { ["Root"] = root } };

    [Fact]
    public async Task MissingRoot_AndItsMissingParents_AreNotLeftBehind()
    {
        var root = Path.Combine(_temp, "a", "b", "c");

        var result = await Provider().TestAsync(Config(root), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("does not exist yet");
        Directory.EnumerateFileSystemEntries(_temp).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistingRoot_KeepsItsOwnContent_AndLosesOnlyTheProbeFolders()
    {
        File.WriteAllText(Path.Combine(_temp, "package.zip"), "x");

        var result = await Provider().TestAsync(Config(_temp), CancellationToken.None);

        result.Success.ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(_temp).Select(Path.GetFileName).ShouldBe(new[] { "package.zip" });
    }

    [Fact]
    public async Task ProbeFolderHoldingSomethingElse_IsNotRemoved()
    {
        var probeFolder = Path.Combine(_temp, ".squid-probe");

        Directory.CreateDirectory(probeFolder);
        File.WriteAllText(Path.Combine(probeFolder, "keep.me"), "x");

        await Provider().TestAsync(Config(_temp), CancellationToken.None);

        File.Exists(Path.Combine(probeFolder, "keep.me")).ShouldBeTrue();
    }

    [Fact]
    public async Task SettingsCheck_AddsTheProvidersWarningsToTheResult()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"warn-{Guid.NewGuid():N}");

        var result = await PackageStoreSettingsCheck.TestAsync(Provider(), new Dictionary<string, string> { ["Root"] = root }, new Dictionary<string, string>(), StoredSecrets.None, CancellationToken.None);

        result.Warnings.ShouldContain(w => w.Contains("inside the application folder"));
    }
}
