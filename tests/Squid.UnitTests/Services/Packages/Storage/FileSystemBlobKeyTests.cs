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
    [InlineData(".SQUID-TMP/x")]
    [InlineData(".Squid-Tmp/deep/x")]
    [InlineData("a:b")]
    [InlineData("acme/b:c.zip")]
    [InlineData("C:/x")]
    [InlineData("con")]
    [InlineData("CON.txt")]
    [InlineData("a/nul")]
    [InlineData("a/Nul.tar.gz")]
    [InlineData("lpt1.log")]
    [InlineData("acme/COM9/x.zip")]
    [InlineData("aux/x")]
    [InlineData("prn")]
    [InlineData("a./b")]
    [InlineData("trailingdot.")]
    [InlineData("a /b")]
    [InlineData("dir/file ")]
    [InlineData("a\tb")]
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
    [InlineData("console/x.zip")]
    [InlineData("a/com0/x")]
    [InlineData("a/lpt10")]
    [InlineData("conx")]
    [InlineData("a/.b")]
    [InlineData("a/..b")]
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

    [Theory]
    [InlineData("/r", "/r/a", true)]
    [InlineData("/r", "/r/a/b/c.zip", true)]
    [InlineData("/r/", "/r/a", true)]
    [InlineData("/r", "/r", false)]
    [InlineData("/r", "/r2/a", false)]
    [InlineData("/r", "/rx", false)]
    [InlineData("/r/sub", "/r/other", false)]
    [InlineData("/r", "/x/r/a", false)]
    [InlineData("/r", "/", false)]
    public void IsUnder_IsStrictlyInside_AndNotFooledByANamePrefix(string root, string fullPath, bool expected)
    {
        if (Path.DirectorySeparatorChar != '/') return;

        FileSystemBlobKey.IsUnder(root, fullPath).ShouldBe(expected);
    }

    [Theory]
    [InlineData(".squid-tmp/x", true)]
    [InlineData(".SQUID-TMP/x", true)]
    [InlineData(".squid-probe/x", true)]
    [InlineData(".Squid-Probe/x", true)]
    [InlineData(".squid-probex/x", false)]
    [InlineData("a/.squid-probe/x", false)]
    [InlineData("acme/web.zip", false)]
    public void IsHiddenFromListing_CoversTheStoresOwnFolders_InAnyCase(string key, bool hidden)
    {
        FileSystemBlobKey.IsHiddenFromListing(key).ShouldBe(hidden);
    }

    [Theory]
    [InlineData("acme/co")]
    [InlineData("acme/con")]
    [InlineData("acme/b:")]
    public void PrefixesMayStopMidName_SoTheirLastSegmentIsNotHeldToTheFullNameRules(string prefix)
    {
        if (prefix.Contains(':')) return;

        Should.NotThrow(() => FileSystemBlobKey.EnsurePrefixSafe(prefix));
    }

    [Fact]
    public void APrefixStillRejectsAReservedStagingFolder_InAnyCase()
    {
        Should.Throw<InvalidPackageBlobKeyException>(() => FileSystemBlobKey.EnsurePrefixSafe(".SQUID-TMP/"));
    }
}
