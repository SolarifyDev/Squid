using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public static class PodLogEncryptionKeyManager
{
    private const string KeyFileName = ".squid-log-key";

    public static void WriteKeyToWorkspace(string workDir, byte[] machineKey, string ticketId)
    {
        var key = PodLogEncryption.DeriveLogEncryptionKey(machineKey, ticketId);
        var keyPath = Path.Combine(workDir, KeyFileName);
        ResilientFileSystem.WriteAllBytes(keyPath, key);

        Log.Debug("Wrote log encryption key to {KeyPath}", keyPath);
    }

    public static byte[]? ReadKeyFromWorkspace(string workDir)
    {
        var keyPath = Path.Combine(workDir, KeyFileName);

        if (!ResilientFileSystem.FileExists(keyPath))
            return null;

        return ResilientFileSystem.ReadAllBytes(keyPath);
    }
}
