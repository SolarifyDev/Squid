namespace Squid.Tentacle.Watchdog;

public static class NfsHealthChecker
{
    // Aligned with Octopus checkFilesystem(): os.ReadDir(path)
    public static bool CheckFilesystem(string path)
    {
        try
        {
            Directory.GetFiles(path);
            return true;
        }
        catch (Exception ex) when (IsCorruptedMount(ex))
        {
            Console.Error.WriteLine($"Corrupted NFS mount detected: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            // Non-NFS errors are ignored (aligned with Octopus behavior)
            Console.WriteLine($"Non-NFS filesystem error (ignored): {ex.Message}");
            return true;
        }
    }

    // Aligned with Octopus IsCorruptedMnt()
    // ESTALE=116, ENOTCONN=107, EIO=5, EACCES=13, EHOSTDOWN=64, EWOULDBLOCK=35
    public static bool IsCorruptedMount(Exception ex)
    {
        var errno = GetErrno(ex);
        if (errno == null) return false;

        return errno.Value is
            116    // ESTALE — stale NFS file handle
            or 107 // ENOTCONN — transport endpoint not connected
            or 5   // EIO — I/O error
            or 13  // EACCES — permission denied (NFS export issue)
            or 64  // EHOSTDOWN — host is down
            or 35; // EWOULDBLOCK — operation would block
    }

    private static int? GetErrno(Exception ex)
    {
        // IOException.HResult on Linux encodes errno: 0x80070000 | errno
        if (ex is IOException ioEx)
            return ioEx.HResult & 0xFFFF;

        if (ex.InnerException is IOException innerIo)
            return innerIo.HResult & 0xFFFF;

        return null;
    }
}
