using System.IO.Compression;
using System.Text;

namespace Squid.Calamari.Tests.TestSupport;

public static class TestPackageBuilder
{
    public static string CreateZip(string rootDir, IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(rootDir);
        var path = Path.Combine(rootDir, $"pkg-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
        return path;
    }
}
