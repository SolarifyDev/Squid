namespace Squid.Core.Services.DeploymentExecution.Script.Files;

/// <summary>
/// A single file that needs to be materialised on (or alongside) the deployment target
/// before a script runs. The <see cref="RelativePath"/> is always forward-slash
/// separated and anchored at the transport's work directory — e.g.
/// <c>content/values.yaml</c>, <c>deploy.sh</c>, <c>bin/helper.sh</c>.
///
/// <para>
/// Rules enforced by <see cref="EnsureValid"/>:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="RelativePath"/> is not null, empty, or whitespace.</description></item>
///   <item><description>It is not an absolute/rooted path (no leading <c>/</c> or <c>\</c>, no drive letter).</description></item>
///   <item><description>It contains no <c>..</c> segments and no backslashes.</description></item>
///   <item><description>It contains no empty segments (e.g. <c>foo//bar</c>).</description></item>
///   <item><description><see cref="Content"/> is not null (zero-length is allowed).</description></item>
/// </list>
/// </summary>
public sealed record DeploymentFile(
    string RelativePath,
    byte[] Content,
    DeploymentFileKind Kind,
    bool IsExecutable = false)
{
    private static readonly char[] SeparatorChars = { '/', '\\' };

    public static DeploymentFile Script(string relativePath, byte[] content, bool isExecutable = true)
        => new(relativePath, content, DeploymentFileKind.Script, isExecutable);

    public static DeploymentFile Asset(string relativePath, byte[] content)
        => new(relativePath, content, DeploymentFileKind.Asset);

    public static DeploymentFile Package(string relativePath, byte[] content)
        => new(relativePath, content, DeploymentFileKind.Package);

    public static DeploymentFile Bootstrap(string relativePath, byte[] content, bool isExecutable = true)
        => new(relativePath, content, DeploymentFileKind.Bootstrap, isExecutable);

    public static DeploymentFile RuntimeBundle(string relativePath, byte[] content, bool isExecutable = true)
        => new(relativePath, content, DeploymentFileKind.RuntimeBundle, isExecutable);

    /// <summary>True if the path contains a directory separator (i.e. lives under a subfolder).</summary>
    public bool IsNested => RelativePath.IndexOf('/') >= 0;

    /// <summary>
    /// Validates this file's path and content. Throws <see cref="ArgumentException"/> with a
    /// descriptive message on the first violation.
    /// </summary>
    public void EnsureValid()
    {
        EnsurePathIsNotEmpty();
        EnsurePathIsNotRooted();
        EnsurePathHasNoBackslash();
        EnsurePathHasNoTraversal();
        EnsurePathHasNoEmptySegments();
        EnsureContentIsNotNull();
    }

    private void EnsurePathIsNotEmpty()
    {
        if (string.IsNullOrWhiteSpace(RelativePath))
            throw new ArgumentException("DeploymentFile.RelativePath cannot be null or empty.");
    }

    private void EnsurePathIsNotRooted()
    {
        if (RelativePath.StartsWith('/') || RelativePath.StartsWith('\\'))
            throw new ArgumentException($"DeploymentFile.RelativePath must be relative, not rooted: '{RelativePath}'");

        if (RelativePath.Length >= 2 && RelativePath[1] == ':')
            throw new ArgumentException($"DeploymentFile.RelativePath must not contain a drive letter: '{RelativePath}'");
    }

    private void EnsurePathHasNoBackslash()
    {
        if (RelativePath.Contains('\\'))
            throw new ArgumentException($"DeploymentFile.RelativePath must use forward slashes: '{RelativePath}'");
    }

    private void EnsurePathHasNoTraversal()
    {
        foreach (var segment in RelativePath.Split(SeparatorChars))
        {
            if (segment == "..")
                throw new ArgumentException($"DeploymentFile.RelativePath must not contain '..' segments: '{RelativePath}'");
        }
    }

    private void EnsurePathHasNoEmptySegments()
    {
        var segments = RelativePath.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
                throw new ArgumentException($"DeploymentFile.RelativePath must not contain empty segments: '{RelativePath}'");
        }
    }

    private void EnsureContentIsNotNull()
    {
        if (Content is null)
            throw new ArgumentException($"DeploymentFile.Content for '{RelativePath}' cannot be null (use an empty array for an empty file).");
    }
}
