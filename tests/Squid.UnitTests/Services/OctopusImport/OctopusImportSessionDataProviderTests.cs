using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSessionDataProviderTests
{
    [Fact]
    public async Task AddSessionAsync_InitializesDataVersionAndSourceSummaryBeforeInsert()
    {
        var repository = new Mock<IRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        OctopusImportSession inserted = null;

        repository
            .Setup(r => r.InsertAsync(It.IsAny<OctopusImportSession>(), It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, CancellationToken>((session, _) => inserted = session)
            .Returns(Task.CompletedTask);

        unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var provider = new OctopusImportSessionDataProvider(repository.Object, unitOfWork.Object);
        var session = new OctopusImportSession();

        await provider.AddSessionAsync(session, ct: CancellationToken.None);

        inserted.ShouldBeSameAs(session);
        inserted.DataVersion.ShouldNotBeNull();
        inserted.DataVersion.Length.ShouldBe(16);
        inserted.SourceSummaryJson.ShouldBe("{}");
        inserted.LastStateChangedAt.ShouldNotBe(default);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
