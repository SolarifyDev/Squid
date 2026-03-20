using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService
{
    // Workspace permissions: group-writable (0770/0660).
    // Tentacle runs as root (gid=0), script pods typically belong to root group (gid=0)
    // per OCI/OpenShift best practice. This covers bitnami, alpine, distroless, and OpenShift random-UID images.
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    private const UnixFileMode ExecutableFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    private string PrepareWorkspace(string ticketId, StartScriptCommand command)
    {
        DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(_tentacleSettings.WorkspacePath);

        var workDir = Path.Combine(_tentacleSettings.WorkspacePath, ticketId);
        ResilientFileSystem.CreateDirectory(workDir);
        ResilientFileSystem.SetUnixFileMode(workDir, DirectoryMode);

        WriteScriptFile(workDir, command.ScriptBody);
        WriteAdditionalFiles(workDir, command.Files);

        return workDir;
    }

    private static void WriteScriptFile(string workDir, string scriptBody)
    {
        var scriptPath = Path.Combine(workDir, "script.sh");
        ResilientFileSystem.WriteAllText(scriptPath, scriptBody);
        ResilientFileSystem.SetUnixFileMode(scriptPath, ExecutableFileMode);
    }

    private static void WriteAdditionalFiles(string workDir, List<ScriptFile> files)
    {
        if (files == null) return;

        foreach (var file in files)
        {
            var filePath = Path.Combine(workDir, file.Name);
            var fileDir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(fileDir) && !ResilientFileSystem.DirectoryExists(fileDir))
                ResilientFileSystem.CreateDirectory(fileDir);

            var tempPath = Path.GetTempFileName();

            try
            {
                file.Contents.Receiver()
                    .SaveToAsync(tempPath, CancellationToken.None)
                    .GetAwaiter().GetResult();

                ResilientFileSystem.Move(tempPath, filePath, overwrite: true);
                ResilientFileSystem.SetUnixFileMode(filePath, FileMode);

                if (file.EncryptionPassword != null)
                {
                    ResilientFileSystem.WriteAllText(filePath + ".key", file.EncryptionPassword);
                    ResilientFileSystem.SetUnixFileMode(filePath + ".key", FileMode);
                }
            }
            catch
            {
                if (ResilientFileSystem.FileExists(tempPath))
                    ResilientFileSystem.DeleteFile(tempPath);

                throw;
            }
        }
    }

    private static void CleanupWorkspace(string workDir)
    {
        try
        {
            if (ResilientFileSystem.DirectoryExists(workDir))
                ResilientFileSystem.DeleteDirectory(workDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup workspace {WorkDir}", workDir);
        }
    }
}
