using System.Reflection;

namespace Squid.Core.Services.Machines.Upgrade;

public sealed class BundledTentacleVersionProvider : IBundledTentacleVersionProvider
{
    /// <summary>
    /// Embedded-resource path. Must match the <c>LogicalName</c> in
    /// <c>Squid.Core.csproj</c>'s <c>EmbeddedResource</c> entry. If you rename
    /// the file, update the csproj too — there is no compile-time check that
    /// the resource exists.
    /// </summary>
    private const string EmbeddedVersionResource = "Squid.Core.Resources.Upgrade.bundled-tentacle-version.txt";

    private const string DownloadUrlTemplate =
        "https://github.com/SolarifyDev/Squid/releases/download/{0}/squid-tentacle-{0}-{1}.tar.gz";

    private static readonly Lazy<string> _cachedVersion = new(LoadVersionFromResource);

    public string GetBundledVersion() => _cachedVersion.Value;

    public string GetDownloadUrl(string version, string rid)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is required", nameof(version));
        if (string.IsNullOrWhiteSpace(rid))
            throw new ArgumentException("rid is required", nameof(rid));

        return string.Format(DownloadUrlTemplate, version.Trim(), rid.Trim());
    }

    private static string LoadVersionFromResource()
    {
        var asm = typeof(BundledTentacleVersionProvider).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedVersionResource);

        if (stream == null)
        {
            Log.Warning(
                "Bundled Tentacle version resource '{Resource}' not found in {Assembly}. " +
                "Server cannot recommend an upgrade target — operators must specify TargetVersion explicitly.",
                EmbeddedVersionResource, asm.GetName().Name);
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
