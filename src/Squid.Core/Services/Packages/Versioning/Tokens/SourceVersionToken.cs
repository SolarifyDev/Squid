using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>
/// <c>{source.version}</c>: the version a package declares in its own manifest. Without an argument the manifests are tried in this order: <c>package.json</c>,
/// the <c>*.csproj</c> at the package root, <c>pyproject.toml</c>. <c>{source.version:path}</c> names one manifest explicitly and nothing else is tried.
/// </summary>
public sealed class SourceVersionToken : IVersionTokenSource
{
    public const string TokenName = "source.version";

    private const string PackageJson = "package.json";

    private const string Pyproject = "pyproject.toml";

    public string Token => TokenName;

    public async Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        EnsureContentAvailable(context);

        return argument == null
            ? await ReadFirstDeclaredAsync(context, ct).ConfigureAwait(false)
            : await ReadNamedAsync(context, argument, ct).ConfigureAwait(false);
    }

    private static void EnsureContentAvailable(VersionTokenContext context)
    {
        if (context.TryReadTextFileAsync == null || context.ListRootFileNamesAsync == null)
            throw new VersionTokenValueMissingException(TokenName, "the package content is not available, so no manifest can be read from it");
    }

    private static async Task<string> ReadFirstDeclaredAsync(VersionTokenContext context, CancellationToken ct)
    {
        var candidates = await ListCandidatesAsync(context, ct).ConfigureAwait(false);

        foreach (var path in candidates)
        {
            var version = await TryReadVersionAsync(context, path, ct).ConfigureAwait(false);

            if (version != null) return version;
        }

        throw new VersionTokenValueMissingException(TokenName, $"none of {string.Join(", ", candidates)} in the package declares a version");
    }

    private static async Task<List<string>> ListCandidatesAsync(VersionTokenContext context, CancellationToken ct)
    {
        var root = await context.ListRootFileNamesAsync(ct).ConfigureAwait(false);
        var csproj = root.Where(n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal);

        return [PackageJson, .. csproj, Pyproject];
    }

    private static async Task<string> ReadNamedAsync(VersionTokenContext context, string path, CancellationToken ct)
    {
        if (!StaysInsidePackage(path))
            throw new InvalidVersionTokenArgumentException(TokenName, path, "the path must be relative and inside the package");

        if (!IsSupported(path))
            throw new InvalidVersionTokenArgumentException(TokenName, path, $"supported manifests are {PackageJson}, *.csproj and {Pyproject}");

        var version = await TryReadVersionAsync(context, path, ct).ConfigureAwait(false);

        return version ?? throw new VersionTokenValueMissingException(TokenName, $"'{path}' is missing from the package or declares no version");
    }

    private static async Task<string> TryReadVersionAsync(VersionTokenContext context, string path, CancellationToken ct)
    {
        var text = await context.TryReadTextFileAsync(path, ct).ConfigureAwait(false);

        if (text == null) return null;

        try
        {
            return Parse(path, text);
        }
        catch (FormatException ex)
        {
            throw new VersionTokenValueMissingException(TokenName, $"'{path}' cannot be read: {ex.Message}");
        }
    }

    private static string Parse(string path, string text)
    {
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return ManifestVersionParser.FromCsproj(text);

        return path.EndsWith(Pyproject, StringComparison.OrdinalIgnoreCase) ? ManifestVersionParser.FromPyproject(text) : ManifestVersionParser.FromPackageJson(text);
    }

    private static bool IsSupported(string path)
    {
        var fileName = Path.GetFileName(path);

        return string.Equals(fileName, PackageJson, StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, Pyproject, StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StaysInsidePackage(string path)
    {
        return !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");
    }
}
