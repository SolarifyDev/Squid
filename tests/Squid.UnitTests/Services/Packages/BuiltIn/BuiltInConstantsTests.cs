using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>Values other code, stored data or clients depend on: changing one is a decision, so each is pinned.</summary>
public class BuiltInConstantsTests
{
    [Fact]
    public void FeedType_IsPinned()
    {
        BuiltInFeed.FeedType.ShouldBe("BuiltIn");
    }

    [Fact]
    public void StoreIdProperty_IsPinned()
    {
        BuiltInFeed.StoreIdProperty.ShouldBe("StoreId");
    }

    [Fact]
    public void MaxPackageSize_IsTwoGiB()
    {
        BuiltInFeed.MaxPackageSizeBytes.ShouldBe(2147483648L);
    }

    [Theory]
    [InlineData("BuiltIn", true)]
    [InlineData("builtin", true)]
    [InlineData("BUILTIN", true)]
    [InlineData("NuGet", false)]
    [InlineData("MyBuiltIn", false)]
    [InlineData("BuiltIn Feed", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBuiltIn_IsAnExactMatch_NotASubstring(string feedType, bool expected)
    {
        BuiltInFeed.IsBuiltIn(feedType).ShouldBe(expected);
    }

    [Fact]
    public void BlobKey_IsShaPrefixedByItsFirstTwoHexDigits_WithTheExtension()
    {
        var sha = "ab" + new string('c', 62);

        BuiltInBlobKey.For(sha, ".zip").ShouldBe($"sha256/ab/{sha}.zip");
        BuiltInBlobKey.For(sha, ".tar.gz").ShouldBe($"sha256/ab/{sha}.tar.gz");
    }

    [Fact]
    public void FeedService_NameAndSlug_ArePinned()
    {
        BuiltInFeedService.FeedName.ShouldBe("Built-in feed");
        BuiltInFeedService.FeedSlug.ShouldBe("builtin");
    }
}
