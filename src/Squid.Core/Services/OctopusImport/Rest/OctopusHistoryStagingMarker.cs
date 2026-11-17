using System.Text;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Ties a history fetch's cursor to the staged documents it counted, both kept in the database. The first chunk stores a random id with
/// the session's files and on the cursor; every later chunk, on whichever server, must find that id stored before it reads or stages
/// anything. A staging that is gone (the session was cleaned up) or that an earlier Squid version kept on one server's disk is refused,
/// so a bundle is never built from a staging that silently lacks what earlier chunks counted.
/// </summary>
public static class OctopusHistoryStagingMarker
{
    public const string FileName = OctopusImportSessionFilePaths.Marker;

    public static async Task EnsureIntactAsync(IOctopusImportSessionFileStore files, Guid sessionId, OctopusImportHistoryFetchDto cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cursor.StagingId))
        {
            await StampAsync(files, sessionId, cursor, ct).ConfigureAwait(false);
            return;
        }

        if (await ReadOrNullAsync(files, sessionId, ct).ConfigureAwait(false) != cursor.StagingId)
            throw new OctopusImportHistoryStagingLostException();
    }

    // A cursor no chunk has stamped: a new fetch, or one an older Squid created before its first chunk ran (it cannot be checked, so it is stamped).
    private static async Task StampAsync(IOctopusImportSessionFileStore files, Guid sessionId, OctopusImportHistoryFetchDto cursor, CancellationToken ct)
    {
        var stagingId = Guid.NewGuid().ToString("N");

        await files.WriteAsync(sessionId, FileName, new MemoryStream(Encoding.UTF8.GetBytes(stagingId)), OctopusImportSessionFileEncoding.Identity, ct).ConfigureAwait(false);

        cursor.StagingId = stagingId;
    }

    private static async Task<string> ReadOrNullAsync(IOctopusImportSessionFileStore files, Guid sessionId, CancellationToken ct)
    {
        var stored = await files.ReadAsync(sessionId, FileName, ct).ConfigureAwait(false);

        return stored == null ? null : Encoding.UTF8.GetString(stored).Trim();
    }
}
