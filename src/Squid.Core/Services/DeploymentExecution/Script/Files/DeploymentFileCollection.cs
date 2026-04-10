using System.Collections;

namespace Squid.Core.Services.DeploymentExecution.Script.Files;

/// <summary>
/// Immutable, validated collection of <see cref="DeploymentFile"/> instances. Enforces:
/// <list type="bullet">
///   <item><description>Every file passes <see cref="DeploymentFile.EnsureValid"/>.</description></item>
///   <item><description>No two files share the same <see cref="DeploymentFile.RelativePath"/>
///   (case-insensitive to match typical POSIX + Windows target filesystems).</description></item>
/// </list>
/// </summary>
public sealed class DeploymentFileCollection : IReadOnlyList<DeploymentFile>
{
    public static DeploymentFileCollection Empty { get; } = new(Array.Empty<DeploymentFile>());

    private readonly IReadOnlyList<DeploymentFile> _files;

    public DeploymentFileCollection(IEnumerable<DeploymentFile> files)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));

        var materialized = files.ToList();

        EnsureAllFilesValid(materialized);
        EnsureNoDuplicatePaths(materialized);

        _files = materialized;
    }

    /// <summary>
    /// Converts a legacy <see cref="Dictionary{TKey, TValue}"/>-shaped file map to a typed
    /// <see cref="DeploymentFileCollection"/>. Every entry is classified as
    /// <see cref="DeploymentFileKind.Asset"/>. Used as a bridge while the handler layer
    /// still emits raw dictionaries (Phase 1 of the execution-layer refactor).
    /// </summary>
    public static DeploymentFileCollection FromLegacyFiles(IReadOnlyDictionary<string, byte[]>? files)
    {
        if (files is null || files.Count == 0)
            return Empty;

        var entries = files.Select(kvp => DeploymentFile.Asset(kvp.Key, kvp.Value));

        return new DeploymentFileCollection(entries);
    }

    public int Count => _files.Count;
    public DeploymentFile this[int index] => _files[index];

    public bool Any() => _files.Count > 0;
    public bool HasNestedPaths() => _files.Any(f => f.IsNested);

    public IEnumerator<DeploymentFile> GetEnumerator() => _files.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void EnsureAllFilesValid(List<DeploymentFile> files)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i] is null)
                throw new ArgumentException($"DeploymentFileCollection[{i}] is null.");

            files[i].EnsureValid();
        }
    }

    private static void EnsureNoDuplicatePaths(List<DeploymentFile> files)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!seen.Add(file.RelativePath))
                throw new ArgumentException($"DeploymentFileCollection contains duplicate path: '{file.RelativePath}'");
        }
    }
}
