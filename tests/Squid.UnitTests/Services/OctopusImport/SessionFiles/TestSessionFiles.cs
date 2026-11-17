using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport.SessionFiles;

/// <summary>
/// One in-memory session file store for the fetch tests, which name a session by its directory: the session id is derived from the
/// directory, so every test (and every "server" a test simulates with another directory of the same session) addresses its own files.
/// </summary>
public static class TestSessionFiles
{
    public static InMemoryOctopusImportSessionFileStore Store { get; } = new();

    public static Guid IdOf(string sessionDirectory) => new(MD5.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sessionDirectory))));

    public static OctopusHistoryBundleWriter Writer(OctopusImportSizeLimits limits = null) => new(Store, limits);

    public static OctopusImportHistoryDetailsStore Details() => new(Store);

    /// <summary>The staged document paths of the session (<c>history-staging/{Kind}/{hash}.json</c>).</summary>
    public static IReadOnlyList<string> Staged(string sessionDirectory, string kind = null)
        => Store.Paths(IdOf(sessionDirectory)).Where(p => p.StartsWith(OctopusImportSessionFilePaths.StagingPrefix + (kind == null ? string.Empty : kind + "/"), StringComparison.Ordinal)).ToList();

    public static IReadOnlyList<string> Details(string sessionDirectory) => Store.Paths(IdOf(sessionDirectory)).Where(p => p.StartsWith(OctopusImportSessionFilePaths.DetailsPrefix, StringComparison.Ordinal)).ToList();
}

/// <summary>An upload store with no stored copies: every session reads its registered upload path, as a session uploaded on this server before files were stored with sessions.</summary>
public static class TestUploadStores
{
    public static IOctopusImportTemporaryUploadStore LocalOnly()
    {
        var settings = new Mock<IOctopusImportTemporaryUploadSettings>();

        settings.SetupGet(s => s.RootPath).Returns(Path.Combine(Path.GetTempPath(), "squid-unit-upload-root"));

        return new OctopusImportTemporaryUploadStore(settings.Object, new InMemoryOctopusImportSessionFileStore());
    }
}
