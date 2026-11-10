using FluentValidation;
using System.Linq;
using Squid.Core.Validators.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.UnitTests.Validators.Deployments.Runbooks;

public class RunbookCommandValidatorTests
{
    [Fact]
    public void Create_ValidCommand_Passes()
    {
        var result = new CreateRunbookCommandValidator().Validate(new CreateRunbookCommand
        {
            Runbook = new CreateRunbookModel
            {
                ProjectId = 10,
                SpaceId = 20,
                Name = "Payment Recovery",
                Description = "Handles failed payments",
                Tags = ["recovery", "payments"]
            }
        });

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, "Recovery")]
    [InlineData(10, "")]
    [InlineData(10, " ")]
    public void Create_InvalidProjectOrName_Fails(int projectId, string name)
    {
        var result = new CreateRunbookCommandValidator().Validate(new CreateRunbookCommand
        {
            Runbook = new CreateRunbookModel
            {
                ProjectId = projectId,
                SpaceId = 20,
                Name = name
            }
        });

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Create_OverlongAndDuplicateTagLimits_AreRejected()
    {
        var tooMany = Enumerable.Range(0, 51).Select(index => $"tag-{index}").ToList();
        var tooLong = new List<string> { new('a', 101) };

        new CreateRunbookCommandValidator().Validate(new CreateRunbookCommand
        {
            Runbook = new CreateRunbookModel
            {
                ProjectId = 10,
                SpaceId = 20,
                Name = "Recovery",
                Tags = tooMany
            }
        }).IsValid.ShouldBeFalse();

        new CreateRunbookCommandValidator().Validate(new CreateRunbookCommand
        {
            Runbook = new CreateRunbookModel
            {
                ProjectId = 10,
                SpaceId = 20,
                Name = "Recovery",
                Tags = tooLong
            }
        }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Update_WithoutSpace_AllowsHeaderInjection()
    {
        var result = new UpdateRunbookCommandValidator().Validate(new UpdateRunbookCommand
        {
            Id = 30,
            ProjectId = 10,
            Runbook = new UpdateRunbookModel
            {
                Name = "Updated"
            }
        });

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(30, 0)]
    public void Update_InvalidIdentity_Fails(int id, int projectId)
    {
        var result = new UpdateRunbookCommandValidator().Validate(new UpdateRunbookCommand
        {
            Id = id,
            ProjectId = projectId,
            Runbook = new UpdateRunbookModel { Name = "Updated" }
        });

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void CopyAndDelete_RequireSourceAndProjectIdentity()
    {
        new CopyRunbookCommandValidator().Validate(new CopyRunbookCommand
        {
            SourceRunbookId = 30,
            ProjectId = 10
        }).IsValid.ShouldBeTrue();

        new CopyRunbookCommandValidator().Validate(new CopyRunbookCommand
        {
            SourceRunbookId = 0,
            ProjectId = 10
        }).IsValid.ShouldBeFalse();

        new DeleteRunbookCommandValidator().Validate(new DeleteRunbookCommand
        {
            Id = 30,
            ProjectId = 10
        }).IsValid.ShouldBeTrue();

        new DeleteRunbookCommandValidator().Validate(new DeleteRunbookCommand
        {
            Id = 30,
            ProjectId = 0
        }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void GetRunbook_ValidRequest_Passes()
    {
        var result = new GetRunbookRequestValidator().Validate(new GetRunbookRequest
        {
            Id = 30,
            ProjectId = 10,
            SpaceId = 20
        });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void GetRunbook_WithoutSpace_AllowsHeaderInjection()
    {
        var result = new GetRunbookRequestValidator().Validate(new GetRunbookRequest
        {
            Id = 30,
            ProjectId = 10
        });

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 10, 20)]
    [InlineData(30, 0, 20)]
    [InlineData(30, 10, 0)]
    public void GetRunbook_InvalidIdentity_Fails(int id, int projectId, int spaceId)
    {
        var result = new GetRunbookRequestValidator().Validate(new GetRunbookRequest
        {
            Id = id,
            ProjectId = projectId,
            SpaceId = spaceId
        });

        result.IsValid.ShouldBeFalse();
    }
}
