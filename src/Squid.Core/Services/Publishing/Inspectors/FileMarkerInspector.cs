using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing.Inspectors;

/// <summary>Which well-known files the package has, looked for at the effective root (and, for index.html, in the usual build output folders).</summary>
public sealed class FileMarkerInspector : IPackageInspector
{
    public static readonly string[] ComposeFileNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    // In order of preference: the first folder holding an index.html is the one reported.
    public static readonly string[] IndexHtmlFolders = ["", "dist", "build", "out", "public"];

    public const string RootFolder = ".";

    public IReadOnlyCollection<string> FilesToProbe { get; } = ["Dockerfile", "squid.yaml", .. ComposeFileNames, .. IndexHtmlFolders.Select(IndexHtmlPath)];

    public Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
    {
        if (context.HasFile("Dockerfile")) context.SetTrue(PackageFactKeys.MarkerDockerfile);

        if (ComposeFileNames.Any(context.HasFile)) context.SetTrue(PackageFactKeys.MarkerCompose);

        if (context.HasFile("squid.yaml")) context.SetTrue(PackageFactKeys.MarkerSquidYaml);

        RecordIndexHtml(context);

        return Task.CompletedTask;
    }

    private static string IndexHtmlPath(string folder) => folder.Length == 0 ? "index.html" : folder + "/index.html";

    private static void RecordIndexHtml(PackageInspectionContext context)
    {
        var folder = IndexHtmlFolders.FirstOrDefault(f => context.HasFile(IndexHtmlPath(f)));

        if (folder == null) return;

        context.SetTrue(PackageFactKeys.MarkerIndexHtml);
        context.Set(PackageFactKeys.MarkerIndexHtmlDir, folder.Length == 0 ? RootFolder : folder);
    }
}
