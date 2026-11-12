using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportPreviewActionsTests
{
    [Theory]
    [InlineData(OctopusImportPreviewAction.Create, true, true)]
    [InlineData(OctopusImportPreviewAction.ReuseExisting, true, false)]
    [InlineData(OctopusImportPreviewAction.AlreadyImported, true, false)]
    [InlineData(OctopusImportPreviewAction.Update, true, true)]
    [InlineData(OctopusImportPreviewAction.RenameRequired, false, false)]
    [InlineData(OctopusImportPreviewAction.Skip, false, false)]
    [InlineData(OctopusImportPreviewAction.Unsupported, false, false)]
    [InlineData(OctopusImportPreviewAction.Blocked, false, false)]
    public void ResolvesToDestination_Theory(OctopusImportPreviewAction action, bool resolves, bool writes)
    {
        OctopusImportPreviewActions.ResolvesToDestination(action).ShouldBe(resolves);
        OctopusImportPreviewActions.WritesDestination(action).ShouldBe(writes);
    }

    [Fact]
    public void EveryPreviewAction_IsClassifiedByTheTheory()
    {
        // A new action must be added to ResolvesToDestination_Theory, so its meaning is decided on purpose.
        Enum.GetValues<OctopusImportPreviewAction>().Length.ShouldBe(8);
    }
}
