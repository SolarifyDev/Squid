using FluentValidation;
using Squid.Core.Validators.Deployments.Projects;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Models.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.Projects;

public class CreateProjectCommandValidatorTests
{
    private readonly CreateProjectCommandValidator _validator = new();

    [Fact]
    public void Valid_Command_Passes()
    {
        var command = new CreateProjectCommand
        {
            Project = new ProjectDto { Name = "Test Project", LifecycleId = 1, ProjectGroupId = 1 }
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Null_Project_Fails()
    {
        var command = new CreateProjectCommand { Project = null };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Project");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Name_Fails(string name)
    {
        var command = new CreateProjectCommand
        {
            Project = new ProjectDto { Name = name, LifecycleId = 1, ProjectGroupId = 1 }
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Project.Name");
    }

    [Fact]
    public void Name_Exceeds_MaxLength_Fails()
    {
        var command = new CreateProjectCommand
        {
            Project = new ProjectDto { Name = new string('a', 201), LifecycleId = 1, ProjectGroupId = 1 }
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Project.Name");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_LifecycleId_Fails(int lifecycleId)
    {
        var command = new CreateProjectCommand
        {
            Project = new ProjectDto { Name = "Test", LifecycleId = lifecycleId, ProjectGroupId = 1 }
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Project.LifecycleId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ProjectGroupId_Fails(int projectGroupId)
    {
        var command = new CreateProjectCommand
        {
            Project = new ProjectDto { Name = "Test", LifecycleId = 1, ProjectGroupId = projectGroupId }
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Project.ProjectGroupId");
    }
}
