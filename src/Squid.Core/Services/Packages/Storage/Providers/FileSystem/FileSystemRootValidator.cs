using System.Text.RegularExpressions;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>
/// Pure checks on the root folder an operator typed, run before a store is saved or tested so that no row is written that cannot be resolved later
/// (and a typo cannot point the store at a system folder). An empty root is always fine: it means the default location.
/// </summary>
public static partial class FileSystemRootValidator
{
    private static readonly HashSet<string> UnixSystemFolders = new(StringComparer.Ordinal)
    {
        "/", "/etc", "/bin", "/sbin", "/usr", "/var", "/var/lib", "/lib", "/lib64", "/boot", "/dev", "/proc", "/sys", "/root", "/home",
        "/System", "/Library", "/Applications", "/Users", "/private", "/Volumes"
    };

    /// <summary>What is wrong with the root, empty when it is usable. Never throws.</summary>
    public static List<string> Validate(string configuredRoot, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot)) return [];

        var root = configuredRoot.Trim();

        if (HasInvalidCharacters(root))
            return [$"\"Root folder\" contains a character that is not allowed in a path"];

        var problems = new List<string>();

        if (IsSystemFolder(root))
            problems.Add($"\"Root folder\" is a system folder or a drive root ({root}); choose a dedicated folder for packages");

        if (!Path.IsPathRooted(root))
            problems.Add("\"Root folder\" must be an absolute path");

        if (problems.Count == 0)
            problems.AddRange(ValidateResolved(root, baseDirectory));

        return problems;
    }

    /// <summary>Whether the path is a filesystem root or one of a small explicit list of system folders. Understands Unix and Windows spellings on any OS.</summary>
    public static bool IsSystemFolder(string path)
    {
        var normalized = path.Replace('\\', '/');

        if (normalized.Length > 1) normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0) normalized = "/";

        return UnixSystemFolders.Contains(normalized) || WindowsSystemFolder().IsMatch(normalized);
    }

    private static List<string> ValidateResolved(string root, string baseDirectory)
    {
        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return [$"\"Root folder\" is not a valid path: {ex.Message}"];
        }

        if (IsSystemFolder(full))
            return [$"\"Root folder\" is a system folder or a drive root ({full}); choose a dedicated folder for packages"];

        return ContainsApplicationFolder(full, baseDirectory) ? [$"\"Root folder\" ({full}) contains the application folder; choose a folder outside it"] : [];
    }

    // An explicit root may sit inside the application folder (with a warning), but not be it or hold it: that would put the application's own files in the store.
    private static bool ContainsApplicationFolder(string fullRoot, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return false;

        var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return application.Equals(fullRoot, comparison) || application.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool HasInvalidCharacters(string root) => root.Any(char.IsControl) || root.IndexOfAny(Path.GetInvalidPathChars()) >= 0;

    [GeneratedRegex(@"^[A-Za-z]:(/(Windows|Program Files|Program Files \(x86\)|ProgramData|Users))?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsSystemFolder();
}
