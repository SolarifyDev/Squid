using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationProgressesTests
{
    private static readonly Guid Progress = Guid.NewGuid();

    [Theory]
    [InlineData(OctopusImportSessionState.Importing, "progress", null, true)]      // the running attempt
    [InlineData(OctopusImportSessionState.Importing, "other", null, false)]        // a newer attempt holds it: an older run's progress is stale
    [InlineData(OctopusImportSessionState.Succeeded, "progress", null, true)]      // the attempt that settled keeps its id
    [InlineData(OctopusImportSessionState.Failed, "progress", null, true)]
    [InlineData(OctopusImportSessionState.Validated, null, "progress", true)]      // rolled back: the failure names the attempt
    [InlineData(OctopusImportSessionState.Validated, null, "other", false)]        // a never-started failure has an id of its own
    [InlineData(OctopusImportSessionState.Validated, null, null, false)]           // re-validated: no failure, no progress
    [InlineData(OctopusImportSessionState.Expired, "progress", "progress", false)]
    public void Describes(OctopusImportSessionState state, string confirmationAttempt, string failureAttempt, bool expected)
    {
        OctopusImportConfirmationProgresses.Describes(state, Id(confirmationAttempt), Id(failureAttempt), Progress).ShouldBe(expected);
    }

    // The recovery sweep returned a session whose server died mid-import: its last write still says Importing.
    [Fact]
    public void Settled_ValidatedButUnfinished_ShownRolledBack()
    {
        var progress = new OctopusImportConfirmationProgressDto { Stage = OctopusImportConfirmationStage.Importing, Current = new OctopusImportConfirmationItemDto() };

        var settled = OctopusImportConfirmationProgresses.Settled(progress, OctopusImportSessionState.Validated);

        settled.ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.RolledBack), p => p.Finished.ShouldBeTrue(), p => p.Current.ShouldBeNull());
    }

    [Fact]
    public void Settled_Importing_Untouched()
    {
        var progress = new OctopusImportConfirmationProgressDto { Stage = OctopusImportConfirmationStage.Importing };

        OctopusImportConfirmationProgresses.Settled(progress, OctopusImportSessionState.Importing).Stage.ShouldBe(OctopusImportConfirmationStage.Importing);
    }

    private static Guid? Id(string name) => name switch { "progress" => Progress, "other" => Guid.NewGuid(), _ => null };
}
