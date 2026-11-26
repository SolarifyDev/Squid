using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;
using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>The existing feed commands cannot create, retype or delete the built-in feed, and behave as before for every other feed.</summary>
public class ExternalFeedBuiltInGuardTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IExternalFeedDataProvider> _data = new();
    private readonly ExternalFeedService _sut;

    public ExternalFeedBuiltInGuardTests()
    {
        _sut = new ExternalFeedService(_mapper.Object, _data.Object);
    }

    private void Existing(params ExternalFeed[] feeds)
    {
        _data.Setup(d => d.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync((List<int> ids, CancellationToken _) => feeds.Where(f => ids.Contains(f.Id)).ToList());
    }

    [Theory]
    [InlineData("BuiltIn")]
    [InlineData("builtin")]
    public async Task Create_WithTheBuiltInType_IsRefused(string feedType)
    {
        await Should.ThrowAsync<BuiltInFeedCreationException>(() => _sut.CreateExternalFeedAsync(new CreateExternalFeedCommand { FeedType = feedType }, CancellationToken.None));

        _data.Verify(d => d.AddExternalFeedAsync(It.IsAny<ExternalFeed>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_OfAnotherType_StillWorks()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "NuGet" };

        _mapper.Setup(m => m.Map<ExternalFeed>(It.IsAny<CreateExternalFeedCommand>())).Returns(feed);

        await _sut.CreateExternalFeedAsync(new CreateExternalFeedCommand { FeedType = "NuGet" }, CancellationToken.None);

        _data.Verify(d => d.AddExternalFeedAsync(feed, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("BuiltIn", "builtin")]
    [InlineData("NuGet", "BUILTIN")]
    [InlineData("builtin", "builtin")]
    [InlineData("builtin", "BuiltIn")]
    public async Task Update_AnyCasingOfTheReservedType_IsRefusedUnlessTheExactBuiltInFeedKeepsItsExactType(string from, string to)
    {
        Existing(new ExternalFeed { Id = 7, FeedType = from });

        await Should.ThrowAsync<BuiltInFeedTypeChangeException>(() => _sut.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = 7, FeedType = to }, CancellationToken.None));
    }

    [Theory]
    [InlineData("BuiltIn", "NuGet")]
    [InlineData("NuGet", "BuiltIn")]
    [InlineData("BuiltIn", null)]
    public async Task Update_ChangingToOrFromTheBuiltInType_IsRefused(string from, string to)
    {
        Existing(new ExternalFeed { Id = 7, FeedType = from });

        await Should.ThrowAsync<BuiltInFeedTypeChangeException>(() => _sut.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = 7, FeedType = to }, CancellationToken.None));

        _data.Verify(d => d.UpdateExternalFeedAsync(It.IsAny<ExternalFeed>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_OfTheBuiltInFeed_KeepingItsType_IsAllowed()
    {
        var feed = new ExternalFeed { Id = 7, FeedType = "BuiltIn" };

        Existing(feed);
        _mapper.Setup(m => m.Map(It.IsAny<UpdateExternalFeedCommand>(), feed)).Returns(feed);

        await _sut.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = 7, FeedType = "BuiltIn" }, CancellationToken.None);

        _data.Verify(d => d.UpdateExternalFeedAsync(feed, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_OfAnotherFeed_KeepingItsType_IsAllowed()
    {
        var feed = new ExternalFeed { Id = 8, FeedType = "NuGet" };

        Existing(feed);
        _mapper.Setup(m => m.Map(It.IsAny<UpdateExternalFeedCommand>(), feed)).Returns(feed);

        await _sut.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = 8, FeedType = "NuGet" }, CancellationToken.None);

        _data.Verify(d => d.UpdateExternalFeedAsync(feed, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_IncludingTheBuiltInFeed_DeletesNothing()
    {
        Existing(new ExternalFeed { Id = 1, FeedType = "NuGet" }, new ExternalFeed { Id = 2, FeedType = "BuiltIn" });

        var ex = await Should.ThrowAsync<BuiltInFeedDeletionException>(() => _sut.DeleteExternalFeedsAsync(new DeleteExternalFeedsCommand { Ids = [1, 2] }, CancellationToken.None));

        ex.FeedId.ShouldBe(2);
        _data.Verify(d => d.DeleteExternalFeedsAsync(It.IsAny<List<ExternalFeed>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_OfOtherFeeds_StillWorks()
    {
        Existing(new ExternalFeed { Id = 1, FeedType = "NuGet" });

        await _sut.DeleteExternalFeedsAsync(new DeleteExternalFeedsCommand { Ids = [1] }, CancellationToken.None);

        _data.Verify(d => d.DeleteExternalFeedsAsync(It.Is<List<ExternalFeed>>(l => l.Count == 1), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
