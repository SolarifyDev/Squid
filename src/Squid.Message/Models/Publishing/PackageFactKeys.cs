namespace Squid.Message.Models.Publishing;

/// <summary>The keys of <see cref="PackageFacts.Values"/>. Stored with every pushed package, so a value here is never renamed.</summary>
public static class PackageFactKeys
{
    public const string True = "true";

    // Archive shape
    public const string ArchiveWrapped = "archive.wrapped";
    public const string ArchiveRoot = "archive.root";
    public const string FilesCount = "files.count";
    public const string FilesTruncated = "files.truncated";
    public const string FilesOversized = "files.oversized";

    // File markers (relative to the effective root)
    public const string MarkerDockerfile = "marker.dockerfile";
    public const string MarkerCompose = "marker.compose";
    public const string MarkerIndexHtml = "marker.index-html";
    public const string MarkerIndexHtmlDir = "marker.index-html.dir";
    public const string MarkerSquidYaml = "marker.squid-yaml";

    // Node
    public const string NodePackageJson = "node.package-json";
    public const string NodeBuildScript = "node.build-script";
    public const string NodeStartScript = "node.start-script";
    public const string NodeEngines = "node.engines";
    public const string NodeError = "node.error";

    // squid.yaml
    public const string SquidRecipe = "squid.recipe";
    public const string SquidRunPort = "squid.run.port";
    public const string SquidRunCommand = "squid.run.command";
    public const string SquidHealthPath = "squid.health.path";
    public const string SquidError = "squid.error";

    // An inspector that threw: this prefix followed by the inspector's type name, valued with the error message.
    public const string InspectorFailedPrefix = "inspector.failed.";
}
