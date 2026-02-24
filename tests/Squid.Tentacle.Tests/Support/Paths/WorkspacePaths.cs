namespace Squid.Tentacle.Tests.Support.Paths;

public static class WorkspacePaths
{
    public static string RepositoryRoot => _repositoryRoot.Value;

    public static string SquidTentacleProjectDirectory =>
        Path.Combine(RepositoryRoot, "src", "Squid.Tentacle");

    public static string SquidTentacleAssemblyPath =>
        Path.Combine(SquidTentacleProjectDirectory, "bin", BuildConfiguration, "net9.0", "Squid.Tentacle.dll");

    public static string BuildConfiguration => _buildConfiguration.Value;

    private static readonly Lazy<string> _repositoryRoot = new(FindRepositoryRoot);
    private static readonly Lazy<string> _buildConfiguration = new(ResolveBuildConfiguration);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Squid.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root (Squid.sln) from test base directory.");
    }

    private static string ResolveBuildConfiguration()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

        if (current.Parent == null)
            return "Debug";

        // .../bin/<Configuration>/<TFM>/
        var tfmDirectory = current;
        var configurationDirectory = tfmDirectory.Parent;

        return string.IsNullOrWhiteSpace(configurationDirectory?.Name)
            ? "Debug"
            : configurationDirectory.Name;
    }
}
