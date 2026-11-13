using Squid.Message.Enums.Events;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Enums;

/// <summary>
/// Append-only ordinals: SquidWeb mirrors these enums by ordinal and EventCategory is persisted as smallint,
/// so renumbering silently changes what the UI shows and what stored rows mean.
/// </summary>
public class EnumOrdinalPinTests
{
    [Theory]
    [InlineData(OctopusImportPreviewAction.Create, 0)]
    [InlineData(OctopusImportPreviewAction.ReuseExisting, 1)]
    [InlineData(OctopusImportPreviewAction.RenameRequired, 2)]
    [InlineData(OctopusImportPreviewAction.Skip, 3)]
    [InlineData(OctopusImportPreviewAction.Unsupported, 4)]
    [InlineData(OctopusImportPreviewAction.Blocked, 5)]
    [InlineData(OctopusImportPreviewAction.AlreadyImported, 6)]
    [InlineData(OctopusImportPreviewAction.Update, 7)]
    public void OctopusImportPreviewAction_OrdinalsPinned(OctopusImportPreviewAction action, int expected)
    {
        ((int)action).ShouldBe(expected);
    }

    [Theory]
    [InlineData(OctopusImportResourceOutcomeState.Pending, 0)]
    [InlineData(OctopusImportResourceOutcomeState.Failed, 6)]
    [InlineData(OctopusImportResourceOutcomeState.Updated, 7)]
    public void OctopusImportResourceOutcomeState_OrdinalsPinned(OctopusImportResourceOutcomeState state, int expected)
    {
        ((int)state).ShouldBe(expected);
    }

    [Theory]
    [InlineData(OctopusRestErrorKind.Unauthorized, 0)]
    [InlineData(OctopusRestErrorKind.NotOctopus, 1)]
    [InlineData(OctopusRestErrorKind.Unreachable, 2)]
    [InlineData(OctopusRestErrorKind.TooLarge, 3)]
    [InlineData(OctopusRestErrorKind.Denied, 4)]
    [InlineData(OctopusRestErrorKind.NotFound, 5)]
    [InlineData(OctopusRestErrorKind.Rejected, 6)]
    public void OctopusRestErrorKind_OrdinalsPinned(OctopusRestErrorKind kind, int expected)
    {
        ((int)kind).ShouldBe(expected);
    }

    [Theory]
    [InlineData(OctopusHistoryFetchSkipReason.Rejected, 0)]
    [InlineData(OctopusHistoryFetchSkipReason.Abandoned, 1)]
    public void OctopusHistoryFetchSkipReason_OrdinalsPinned(OctopusHistoryFetchSkipReason reason, int expected)
    {
        ((int)reason).ShouldBe(expected);
    }

    [Fact]
    public void ImportedOctopusEvent_Is100()
    {
        ((short)EventCategory.ImportedOctopusEvent).ShouldBe((short)100);
    }
}
