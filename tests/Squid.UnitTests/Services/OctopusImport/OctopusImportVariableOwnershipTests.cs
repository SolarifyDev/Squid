using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportVariableOwnershipTests
{
    [Theory]
    [InlineData(OctopusImportPreviewAction.Create, true)]
    [InlineData(OctopusImportPreviewAction.AlreadyImported, false)]
    public void AlreadyImportedProjectVariables_NotWritten(OctopusImportPreviewAction variableAction, bool expectedWritten)
    {
        var variableSet = VariableSet("variableset-Projects-1", "Projects-1", "Projects");
        var variable = Variable("Variables-1", variableSet.SourceId);

        var written = OctopusImportVariableOwnership.IsWrittenByImport(variable, Find(variableSet), ActionOf((variable.SourceId, variableAction)));

        written.ShouldBe(expectedWritten);
    }

    [Theory]
    [InlineData(OctopusImportPreviewAction.Create, true)]
    [InlineData(OctopusImportPreviewAction.ReuseExisting, false)]
    [InlineData(OctopusImportPreviewAction.AlreadyImported, false)]
    public void LibraryVariables_WrittenOnlyWhenTheLibraryIsWrittenToo(OctopusImportPreviewAction libraryAction, bool expectedWritten)
    {
        var variableSet = VariableSet("variableset-LibraryVariableSets-1", "LibraryVariableSets-1", "LibraryVariableSet");
        var variable = Variable("Variables-1", variableSet.SourceId);

        var written = OctopusImportVariableOwnership.IsWrittenByImport(variable, Find(variableSet), ActionOf((variable.SourceId, OctopusImportPreviewAction.Create), ("LibraryVariableSets-1", libraryAction)));

        written.ShouldBe(expectedWritten);
    }

    private static Func<string, OctopusResourceNode> Find(OctopusResourceNode variableSet)
        => sourceId => string.Equals(sourceId, variableSet.SourceId, StringComparison.OrdinalIgnoreCase) ? variableSet : null;

    private static Func<string, OctopusImportPreviewAction?> ActionOf(params (string SourceId, OctopusImportPreviewAction Action)[] actions)
        => sourceId => actions.Where(a => string.Equals(a.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)).Select(a => (OctopusImportPreviewAction?)a.Action).FirstOrDefault();

    private static OctopusResourceNode VariableSet(string sourceId, string ownerId, string ownerType)
        => new(sourceId, null, OctopusResourceKind.VariableSet, OctopusDocumentKind.VariableSet, $"{sourceId}.json", null, null, false, new OctopusVariableSetDto { Id = sourceId, OwnerId = ownerId, OwnerType = ownerType });

    private static OctopusResourceNode Variable(string sourceId, string variableSetId)
        => new(sourceId, "Name", OctopusResourceKind.Variable, OctopusDocumentKind.VariableSet, $"{variableSetId}.json", null, variableSetId, false, new OctopusVariableDto { Id = sourceId, Name = "Name" });
}
