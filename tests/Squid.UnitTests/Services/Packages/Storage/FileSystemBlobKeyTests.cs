using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class FileSystemBlobKeyTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "squid-keys-root");

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.zip")]
    [InlineData("a/../../outside.zip")]
    [InlineData("a/..")]
    [InlineData("..")]
    [InlineData("a/./b")]
    [InlineData("a\\b.zip")]
    [InlineData("..\\outside.zip")]
    [InlineData("a//b.zip")]
    [InlineData("a/b/")]
    [InlineData("a\0b.zip")]
    [InlineData(".squid-tmp/x")]
    public void UnsafeKeys_AreRejected(string key)
    {
        Should.Throw<InvalidPackageBlobKeyException>(() => FileSystemBlobKey.ToFullPath(Root, key));
    }

    [Theory]
    [InlineData("a.zip")]
    [InlineData("acme/web/1.0.0.zip")]
    [InlineData("acme/.hidden/x..y.tar.gz")]
    [InlineData("name with spaces/1.0.zip")]
    [InlineData(".squid-probe/abc")]
    [InlineData(".squid-tmpx/abc")]
    public void SafeKeys_ResolveInsideTheRoot(string key)
    {
        var path = FileSystemBlobKey.ToFullPath(Root, key);

        path.ShouldBe(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("acme/")]
    [InlineData("acme/we")]
    public void Prefixes_MayBeEmptyOrPartial(string prefix)
    {
        Should.NotThrow(() => FileSystemBlobKey.EnsurePrefixSafe(prefix));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("/abs")]
    [InlineData("a//")]
    [InlineData("a\\b")]
    public void UnsafePrefixes_AreRejected(string prefix)
    {
        Should.Throw<InvalidPackageBlobKeyException>(() => FileSystemBlobKey.EnsurePrefixSafe(prefix));
    }

    [Fact]
    public void Rejection_NamesTheOffendingKeyAndReason()
    {
        var ex = Should.Throw<InvalidPackageBlobKeyException>(() => FileSystemBlobKey.ToFullPath(Root, "a/../b"));

        ex.Key.ShouldBe("a/../b");
        ex.Reason.ShouldNotBeNullOrWhiteSpace();
    }
}
