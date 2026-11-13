using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusBuiltInFeedsTests
{
    // Octopus fixes these per space; renaming one here would import (or fail on) Octopus's own repository.
    [Fact]
    public void TypesAndIds_Pinned()
    {
        OctopusBuiltInFeeds.FeedTypes.ShouldBe(["BuiltIn", "OctopusProject"]);
        OctopusBuiltInFeeds.FeedIds.ShouldBe(["feeds-builtin", "feeds-builtin-releases"]);
    }

    [Theory]
    [InlineData("BuiltIn", true)]
    [InlineData(" builtin ", true)]
    [InlineData("OctopusProject", true)]
    [InlineData("NuGet", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBuiltInType(string feedType, bool expected) => OctopusBuiltInFeeds.IsBuiltInType(feedType).ShouldBe(expected);

    [Theory]
    [InlineData("feeds-builtin", true)]
    [InlineData("Feeds-BuiltIn", true)]
    [InlineData("feeds-builtin-releases", true)]
    [InlineData("feeds-builtin-0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("feeds-builtin-releases-0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("Feeds-1", false)]
    [InlineData("feeds-builtin-other", false)]
    [InlineData(null, false)]
    public void IsBuiltInId(string feedId, bool expected) => OctopusBuiltInFeeds.IsBuiltInId(feedId).ShouldBe(expected);

    // The REST normaliser and the preview use one list, so both sources exclude the same feeds.
    [Fact]
    public void RestNormaliser_UsesTheSameTypes()
        => OctopusRestDocumentNormalizer.BuiltInFeedTypes.ShouldBeSameAs(OctopusBuiltInFeeds.FeedTypes);
}
