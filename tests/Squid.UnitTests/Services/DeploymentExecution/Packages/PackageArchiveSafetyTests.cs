using System.IO;
using System.IO.Compression;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Packages;

namespace Squid.UnitTests.Services.DeploymentExecution.Packages;

public class PackageArchiveSafetyTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("foo/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    public void EnsureArchiveEntriesAreSafe_RejectsZipSlipEntries(string hostileEntry)
    {
        var bytes = CreateZip(
            ("ok.txt", "ok"),
            (hostileEntry, "x"));

        var ex = Should.Throw<InvalidOperationException>(() =>
            PackageArchiveSafety.EnsureArchiveEntriesAreSafe(bytes, "Acme.Web", "1.0.0"));

        ex.Message.ShouldContain("zip-slip", Case.Insensitive);
        ex.Message.ShouldContain(hostileEntry);
    }

    [Fact]
    public void EnsureArchiveEntriesAreSafe_AllowsSafeZipEntries()
    {
        var bytes = CreateZip(
            ("marker.txt", "ok"),
            ("nested/appsettings.json", "{}"));

        Should.NotThrow(() =>
            PackageArchiveSafety.EnsureArchiveEntriesAreSafe(bytes, "Acme.Web", "1.0.0"));
    }

    [Theory]
    [InlineData("../escape.txt", true)]
    [InlineData("foo/../../etc/passwd", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData("safe/nested.txt", false)]
    [InlineData("safe/../still-inside.txt", true)]
    public void IsUnsafeArchiveEntry_ClassifiesTraversal(string entry, bool expectedUnsafe)
    {
        PackageArchiveSafety.IsUnsafeArchiveEntry(entry).ShouldBe(expectedUnsafe);
    }

    private static byte[] CreateZip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Content);
            }
        }

        return ms.ToArray();
    }
}
