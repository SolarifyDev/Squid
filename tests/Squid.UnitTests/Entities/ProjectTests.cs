using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Xunit;

namespace Squid.UnitTests.Entities;

public class ProjectTests
{
    [Fact]
    public void GetIncludedLibraryVariableSetIdList_Empty_ReturnsEmptyList()
    {
        var project = new Project { VariableSetId = 100, IncludedLibraryVariableSetIds = "" };

        var result = project.GetIncludedLibraryVariableSetIdList();

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetIncludedLibraryVariableSetIdList_Null_ReturnsEmptyList()
    {
        var project = new Project { VariableSetId = 100, IncludedLibraryVariableSetIds = null };

        var result = project.GetIncludedLibraryVariableSetIdList();

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetIncludedLibraryVariableSetIdList_Whitespace_ReturnsEmptyList()
    {
        var project = new Project { VariableSetId = 100, IncludedLibraryVariableSetIds = "  " };

        var result = project.GetIncludedLibraryVariableSetIdList();

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetIncludedLibraryVariableSetIdList_SingleId_ReturnsSingleElement()
    {
        var project = new Project { VariableSetId = 100, IncludedLibraryVariableSetIds = "[5]" };

        var result = project.GetIncludedLibraryVariableSetIdList();

        result.Count.ShouldBe(1);
        result.ShouldContain(5);
    }

    [Fact]
    public void GetIncludedLibraryVariableSetIdList_MultipleIds_ReturnsAll()
    {
        var project = new Project { VariableSetId = 100, IncludedLibraryVariableSetIds = "[1,2,3]" };

        var result = project.GetIncludedLibraryVariableSetIdList();

        result.Count.ShouldBe(3);
        result.ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public void GetIncludedLibraryVariableSetIdList_DoesNotIncludeProjectVariableSetId()
    {
        var project = new Project { VariableSetId = 100, IncludedLibraryVariableSetIds = "[5,10]" };

        var result = project.GetIncludedLibraryVariableSetIdList();

        result.ShouldNotContain(100);
    }
}
