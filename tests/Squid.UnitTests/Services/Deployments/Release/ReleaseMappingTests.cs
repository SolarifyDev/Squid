using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Models.Deployments.Release;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Release;

public class ReleaseMappingTests
{
    private readonly IMapper _mapper = new MapperConfiguration(cfg => cfg.AddProfile<ReleaseMapping>()).CreateMapper();

    [Fact]
    public void UpdateReleaseModel_NullNote_KeepsExisting()
    {
        // An older client that posts only {version} must not wipe imported or hand-written notes.
        var release = new ReleaseEntity { Version = "1.0.0", ReleaseNote = "Shipped from Octopus" };

        _mapper.Map(new UpdateReleaseModel { Version = "1.0.1" }, release);

        release.Version.ShouldBe("1.0.1");
        release.ReleaseNote.ShouldBe("Shipped from Octopus");
    }

    [Theory]
    [InlineData("New notes", "New notes")]
    [InlineData("", "")]
    public void UpdateReleaseModel_NonNullNote_Overwrites(string posted, string expected)
    {
        var release = new ReleaseEntity { Version = "1.0.0", ReleaseNote = "Old notes" };

        _mapper.Map(new UpdateReleaseModel { Version = "1.0.0", ReleaseNote = posted }, release);

        release.ReleaseNote.ShouldBe(expected);
    }

    [Fact]
    public void CreateReleaseCommand_MapsReleaseNote()
    {
        _mapper.Map<ReleaseEntity>(new CreateReleaseCommand { Version = "1.0.0", ReleaseNote = "notes" }).ReleaseNote.ShouldBe("notes");
    }

    [Fact]
    public void Release_ToDto_CarriesReleaseNote()
    {
        _mapper.Map<ReleaseDto>(new ReleaseEntity { ReleaseNote = "notes" }).ReleaseNote.ShouldBe("notes");
    }
}
