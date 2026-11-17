using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Ties a history fetch's cursor (kept in the database) to the session directory its documents are staged in (kept on one server's
/// disk). The first chunk writes a random id into the directory and onto the cursor; every later chunk must find that id there before
/// it reads or stages anything. A directory that is gone (restart on temporary storage) or another one (another replica) is refused,
/// so a bundle is never built from a staging that silently lacks what earlier chunks counted.
/// </summary>
public static class OctopusHistoryStagingMarker
{
    public const string FileName = "staging.id";

    public static void EnsureIntact(string sessionDirectory, OctopusImportHistoryFetchDto cursor)
    {
        var path = Path.Combine(sessionDirectory, FileName);

        if (string.IsNullOrWhiteSpace(cursor.StagingId))
        {
            Stamp(path, cursor);
            return;
        }

        if (ReadOrNull(path) != cursor.StagingId)
            throw new OctopusImportHistoryStagingLostException();
    }

    // A cursor no chunk has stamped: a new fetch, or one an older Squid started (it cannot be checked, so it is stamped, never refused).
    private static void Stamp(string path, OctopusImportHistoryFetchDto cursor)
    {
        var stagingId = Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, stagingId);

        cursor.StagingId = stagingId;
    }

    private static string ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
