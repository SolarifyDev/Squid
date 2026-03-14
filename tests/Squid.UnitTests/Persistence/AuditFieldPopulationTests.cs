using System;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Message.Constants;

namespace Squid.UnitTests.Persistence;

public class AuditFieldPopulationTests
{
    private static SquidDbContext CreateContext(ICurrentUser currentUser = null)
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SquidDbContext(options, currentUser);
    }

    [Fact]
    public async Task ApplyAuditFields_OnAdded_SetsAllFourFields()
    {
        var mockUser = new Mock<ICurrentUser>();
        mockUser.Setup(u => u.Id).Returns(42);

        using var context = CreateContext(mockUser.Object);

        var channel = new Channel { Name = "test", ProjectId = 1, SpaceId = 1, Slug = "test" };
        context.Set<Channel>().Add(channel);

        var before = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
        var after = DateTimeOffset.UtcNow;

        channel.CreatedDate.ShouldBeInRange(before, after);
        channel.CreatedBy.ShouldBe(42);
        channel.LastModifiedDate.ShouldBeInRange(before, after);
        channel.LastModifiedBy.ShouldBe(42);
    }

    [Fact]
    public async Task ApplyAuditFields_OnModified_SetsOnlyLastModifiedFields()
    {
        var mockUser = new Mock<ICurrentUser>();
        mockUser.Setup(u => u.Id).Returns(42);

        using var context = CreateContext(mockUser.Object);

        var channel = new Channel { Name = "test", ProjectId = 1, SpaceId = 1, Slug = "test" };
        context.Set<Channel>().Add(channel);
        await context.SaveChangesAsync();

        var originalCreatedDate = channel.CreatedDate;
        var originalCreatedBy = channel.CreatedBy;

        mockUser.Setup(u => u.Id).Returns(99);
        channel.Name = "updated";

        await context.SaveChangesAsync();

        channel.CreatedDate.ShouldBe(originalCreatedDate);
        channel.CreatedBy.ShouldBe(originalCreatedBy);
        channel.LastModifiedBy.ShouldBe(99);
        channel.LastModifiedDate.ShouldBeGreaterThanOrEqualTo(originalCreatedDate);
    }

    [Fact]
    public async Task ApplyAuditFields_NullUserId_FallsBackToInternalUser()
    {
        var mockUser = new Mock<ICurrentUser>();
        mockUser.Setup(u => u.Id).Returns((int?)null);

        using var context = CreateContext(mockUser.Object);

        var channel = new Channel { Name = "test", ProjectId = 1, SpaceId = 1, Slug = "test" };
        context.Set<Channel>().Add(channel);
        await context.SaveChangesAsync();

        channel.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
        channel.LastModifiedBy.ShouldBe(CurrentUsers.InternalUser.Id);
    }

    [Fact]
    public async Task ApplyAuditFields_NoCurrentUser_FallsBackToInternalUser()
    {
        using var context = CreateContext(null);

        var channel = new Channel { Name = "test", ProjectId = 1, SpaceId = 1, Slug = "test" };
        context.Set<Channel>().Add(channel);
        await context.SaveChangesAsync();

        channel.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
        channel.LastModifiedBy.ShouldBe(CurrentUsers.InternalUser.Id);
    }
}
