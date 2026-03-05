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
        var workDir = Path.Combine(_tentacleSettings.WorkspacePath, ticketId);
        Directory.CreateDirectory(workDir);
        SetUnixMode(workDir, DirectoryMode);

        WriteScriptFile(workDir, command.ScriptBody);
        WriteAdditionalFiles(workDir, command.Files);

        return workDir;
    }

    private static void WriteScriptFile(string workDir, string scriptBody)
    {
        var scriptPath = Path.Combine(workDir, "script.sh");
        File.WriteAllText(scriptPath, scriptBody);
        SetUnixMode(scriptPath, ExecutableFileMode);
    }

    private static void WriteAdditionalFiles(string workDir, List<ScriptFile> files)
    {
        if (files == null) return;

        foreach (var file in files)
        {
            var filePath = Path.Combine(workDir, file.Name);
            var fileDir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                Directory.CreateDirectory(fileDir);

            var tempPath = Path.GetTempFileName();

            try
            {
                file.Contents.Receiver()
                    .SaveToAsync(tempPath, CancellationToken.None)
                    .GetAwaiter().GetResult();

                File.Move(tempPath, filePath, overwrite: true);
                SetUnixMode(filePath, FileMode);

                if (file.EncryptionPassword != null)
                {
                    File.WriteAllText(filePath + ".key", file.EncryptionPassword);
                    SetUnixMode(filePath + ".key", FileMode);
                }
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                throw;
            }
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set permissions on {Path}", path);
        }
    }

    private static void CleanupWorkspace(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup workspace {WorkDir}", workDir);
        }
    }
}
