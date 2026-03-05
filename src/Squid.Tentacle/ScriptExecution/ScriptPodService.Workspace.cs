using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService
{
    private string PrepareWorkspace(string ticketId, StartScriptCommand command)
    {
        var workDir = Path.Combine(_tentacleSettings.WorkspacePath, ticketId);
        Directory.CreateDirectory(workDir);

        WriteScriptFile(workDir, command.ScriptBody);
        WriteAdditionalFiles(workDir, command.Files);

        return workDir;
    }

    private static void WriteScriptFile(string workDir, string scriptBody)
    {
        var scriptPath = Path.Combine(workDir, "script.sh");
        File.WriteAllText(scriptPath, scriptBody);
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

                if (file.EncryptionPassword != null)
                    File.WriteAllText(filePath + ".key", file.EncryptionPassword);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                throw;
            }
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
