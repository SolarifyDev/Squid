namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>
/// Puts a folder back as the probe found it: the probe and staging folders a test leaves behind are removed once empty, and so is a root (with the
/// parents created for it) that did not exist, so testing a mistyped path leaves nothing on disk.
/// </summary>
public sealed class FileSystemProbeCleanup
{
    private readonly string _root;
    private readonly string _nearestExistingAncestor;

    private FileSystemProbeCleanup(string root, bool rootExisted, string nearestExistingAncestor)
    {
        _root = root;
        RootExisted = rootExisted;
        _nearestExistingAncestor = nearestExistingAncestor;
    }

    public bool RootExisted { get; }

    public static FileSystemProbeCleanup Begin(string root)
    {
        var existed = Directory.Exists(root);

        return new FileSystemProbeCleanup(root, existed, existed ? null : FindNearestExistingAncestor(root));
    }

    public void Finish()
    {
        TryDeleteEmpty(Path.Combine(_root, PackageBlobStoreProbe.KeyPrefix.TrimEnd('/')));
        TryDeleteEmpty(Path.Combine(_root, FileSystemBlobKey.StagingFolderName));

        if (!RootExisted) RemoveCreatedFolders();
    }

    private void RemoveCreatedFolders()
    {
        var folder = _root;

        while (folder != null && folder != _nearestExistingAncestor && TryDeleteEmpty(folder))
            folder = Path.GetDirectoryName(folder);
    }

    private static string FindNearestExistingAncestor(string root)
    {
        var folder = Path.GetDirectoryName(root);

        while (folder != null && !Directory.Exists(folder))
            folder = Path.GetDirectoryName(folder);

        return folder;
    }

    private static bool TryDeleteEmpty(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: false);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty (something else lives there) or not ours to remove: leave it.
            return false;
        }
    }
}
