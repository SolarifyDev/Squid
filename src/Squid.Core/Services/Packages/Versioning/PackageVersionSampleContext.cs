using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning;

/// <summary>A made-up upload that every token can read from, used to check a template when it is saved, before any real upload exists.</summary>
public static class PackageVersionSampleContext
{
    public const string SampleVersion = "1.2.3";

    private const string SampleGitSha = "0123456789abcdef0123456789abcdef01234567";

    public static VersionTokenContext Create(int feedId, string packageId)
    {
        return new VersionTokenContext
        {
            FeedId = feedId,
            PackageId = packageId,
            FileName = $"{packageId}.{SampleVersion}.zip",
            InputVersion = SampleVersion,
            GitSha = SampleGitSha,
            TryReadTextFileAsync = (path, _) => Task.FromResult(SampleManifest(path)),
            ListRootFileNamesAsync = _ => Task.FromResult<IReadOnlyList<string>>(["package.json"]),
            DryRun = true
        };
    }

    private static string SampleManifest(string path)
    {
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return $"<Project><PropertyGroup><Version>{SampleVersion}</Version></PropertyGroup></Project>";

        if (path.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase)) return $"[project]\nversion = \"{SampleVersion}\"\n";

        return path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) ? $"{{\"version\":\"{SampleVersion}\"}}" : null;
    }
}
