using Squid.Core.Validators.Deployments.Release;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.UnitTests.Validators.Deployments.Releases;

public class CreateReleaseCommandValidatorTests
{
    private readonly CreateReleaseCommandValidator _validator = new();

    [Fact]
    public void Valid_Command_Passes()
    {
        var command = new CreateReleaseCommand
        {
            Version = "1.0.0",
            ProjectId = 1,
            ChannelId = 1
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Version_Fails(string version)
    {
        var command = new CreateReleaseCommand
        {
            Version = version,
            ProjectId = 1,
            ChannelId = 1
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Version");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ProjectId_Fails(int projectId)
    {
        var command = new CreateReleaseCommand
        {
            Version = "1.0.0",
            ProjectId = projectId,
            ChannelId = 1
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "ProjectId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ChannelId_Fails(int channelId)
    {
        var command = new CreateReleaseCommand
        {
            Version = "1.0.0",
            ProjectId = 1,
            ChannelId = channelId
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "ChannelId");
    }

    [Fact]
    public void SelectedPackage_WithEmptyActionName_Fails()
    {
        var command = new CreateReleaseCommand
        {
            Version = "1.0.0",
            ProjectId = 1,
            ChannelId = 1,
            SelectedPackages =
            [
                new CreateReleaseSelectedPackageDto
                {
                    ActionName = "",
                    Version = "1.2.3"
                }
            ]
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.Contains("ActionName"));
    }

    [Fact]
    public void SelectedPackage_WithEmptyVersion_Fails()
    {
        var command = new CreateReleaseCommand
        {
            Version = "1.0.0",
            ProjectId = 1,
            ChannelId = 1,
            SelectedPackages =
            [
                new CreateReleaseSelectedPackageDto
                {
                    ActionName = "Deploy web",
                    Version = ""
                }
            ]
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.Contains("Version"));
    }
}
