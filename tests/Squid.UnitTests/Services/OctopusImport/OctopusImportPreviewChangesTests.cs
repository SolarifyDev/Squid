using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The pure old → new of an Update / Fill: exactly the fields the history writers rewrite, compared at the fingerprint's precision (state
/// normalised, times in UTC to the millisecond, text trimmed), so a field is listed only when Squid's value really differs.
/// </summary>
public class OctopusImportPreviewChangesTests
{
    private const int TaskRow = 41;
    private const int ReleaseRow = 31;

    private static readonly DateTimeOffset Queued = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 1, 2, 3, 5, 6, TimeSpan.Zero);

    [Theory]
    [InlineData("State", "Failed", "Success", "Failed")]
    [InlineData("State", "Canceled", "Success", "Cancelled")]                       // normalised the way the importer writes it
    [InlineData("QueueTime", "2026-01-02T04:00:00Z", "2026-01-02T03:04:05.000Z", "2026-01-02T04:00:00.000Z")]
    [InlineData("StartTime", "2026-01-02T03:04:07+01:00", "2026-01-02T03:04:06.000Z", "2026-01-02T02:04:07.000Z")]
    [InlineData("CompletedTime", null, "2026-01-02T03:05:06.000Z", null)]
    [InlineData("ErrorMessage", "  boom  ", null, "boom")]
    [InlineData("HasWarningsOrErrors", "true", "false", "true")]
    public void ForHistoryUnit_OneScalarChanged_OnlyThatFieldOldToNew(string field, string incomingValue, string expectedOld, string expectedNew)
    {
        var incoming = Task();

        Set(incoming, field, incomingValue);

        var changes = OctopusImportPreviewChanges.ForHistoryUnit(incoming, StoredUnit(), TaskRow, Stored(StoredTask()));

        changes.Select(c => (c.Field, c.OldValue, c.NewValue)).ShouldBe([(field, expectedOld, expectedNew)]);
    }

    // Sub-millisecond drift (Postgres keeps microseconds, the fingerprint milliseconds) and whitespace are not changes.
    [Fact]
    public void ForHistoryUnit_OnlyPrecisionAndWhitespaceDiffer_NoScalarChange_FallsBackToRecordedIncompletely()
    {
        var stored = StoredTask() with { StartTime = Started.AddTicks(3), ErrorMessage = "" };
        var incoming = Task();

        incoming.ErrorMessage = "   ";

        var changes = OctopusImportPreviewChanges.ForHistoryUnit(incoming, StoredUnit(), TaskRow, Stored(stored));

        changes.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Field.ShouldBe(OctopusImportPreviewChanges.RecordField), c => c.Description.ShouldBe(OctopusImportPreviewChanges.RecordIncompleteDescription));
    }

    [Theory]
    [InlineData(null, "abc", true)]          // first details for this task
    [InlineData("abc", "def", true)]         // details changed
    [InlineData("abc", "abc", false)]
    [InlineData("abc", null, false)]         // a source without details never changes the stored ones
    public void ForHistoryUnit_Details_ListedOnlyWhenTheIncomingDetailsDiffer(string storedDetails, string incomingDetails, bool listed)
    {
        var incoming = Task();

        incoming.DetailFingerprint = incomingDetails;

        var changes = OctopusImportPreviewChanges.ForHistoryUnit(incoming, StoredUnit(storedDetails), TaskRow, Stored(StoredTask()));

        changes.Any(c => c.Field == OctopusImportPreviewChanges.DetailsField).ShouldBe(listed);
    }

    [Theory]
    [InlineData(null)]          // no task row recorded
    [InlineData(99)]            // recorded, but the row is gone
    public void ForHistoryUnit_StoredTaskMissing_WrittenAgain(int? storedTaskId)
    {
        var changes = OctopusImportPreviewChanges.ForHistoryUnit(Task(), StoredUnit(), storedTaskId, Stored(StoredTask()));

        changes.ShouldHaveSingleItem().Field.ShouldBe(OctopusImportPreviewChanges.TaskField);
    }

    [Theory]
    [InlineData("old", "new", "old", "new")]
    [InlineData(null, "new", null, "new")]
    [InlineData("old", null, "old", null)]
    public void ForRelease_NotesChanged_OldToNew(string storedNotes, string incomingNotes, string expectedOld, string expectedNew)
    {
        var stored = new OctopusImportStoredHistory(new Dictionary<int, string> { [ReleaseRow] = storedNotes }, new Dictionary<int, OctopusImportStoredTask>());

        var change = OctopusImportPreviewChanges.ForRelease(new OctopusReleaseDto { ReleaseNotes = incomingNotes }, ReleaseRow, stored).ShouldHaveSingleItem();

        (change.Field, change.OldValue, change.NewValue).ShouldBe((OctopusImportPreviewChanges.ReleaseNotesField, expectedOld, expectedNew));
    }

    [Fact]
    public void ForRelease_SameNotes_FallsBackToRecordedIncompletely()
    {
        var stored = new OctopusImportStoredHistory(new Dictionary<int, string> { [ReleaseRow] = "same" }, new Dictionary<int, OctopusImportStoredTask>());

        OctopusImportPreviewChanges.ForRelease(new OctopusReleaseDto { ReleaseNotes = " same " }, ReleaseRow, stored).ShouldHaveSingleItem().Field.ShouldBe(OctopusImportPreviewChanges.RecordField);
    }

    [Fact]
    public void LongValues_CutForDisplay()
    {
        var stored = new OctopusImportStoredHistory(new Dictionary<int, string> { [ReleaseRow] = "short" }, new Dictionary<int, OctopusImportStoredTask>());

        var change = OctopusImportPreviewChanges.ForRelease(new OctopusReleaseDto { ReleaseNotes = new string('x', 2000) }, ReleaseRow, stored).Single();

        change.NewValue.Length.ShouldBe(OctopusImportPreviewChanges.MaxDisplayLength + 1);
        change.NewValue.ShouldEndWith("…");
    }

    [Theory]
    [InlineData(OctopusResourceKind.Variable, OctopusImportPreviewChanges.VariableValueField)]
    [InlineData(OctopusResourceKind.Feed, OctopusImportPreviewChanges.FeedPasswordField)]
    public void ForSecretFill_NamesTheFieldOnly(OctopusResourceKind kind, string field)
    {
        var change = OctopusImportPreviewChanges.ForSecretFill(kind).ShouldHaveSingleItem();

        change.ShouldSatisfyAllConditions(c => c.Field.ShouldBe(field), c => c.ValueHidden.ShouldBeTrue(), c => c.OldValue.ShouldBeNull(), c => c.NewValue.ShouldBeNull());
    }

    // Field names are what clients show and match on.
    [Fact]
    public void FieldNames_Pinned()
    {
        new[] { OctopusImportPreviewChanges.ReleaseNotesField, OctopusImportPreviewChanges.StateField, OctopusImportPreviewChanges.QueueTimeField, OctopusImportPreviewChanges.StartTimeField, OctopusImportPreviewChanges.CompletedTimeField, OctopusImportPreviewChanges.ErrorMessageField, OctopusImportPreviewChanges.HasWarningsOrErrorsField, OctopusImportPreviewChanges.DetailsField, OctopusImportPreviewChanges.TaskField, OctopusImportPreviewChanges.RecordField, OctopusImportPreviewChanges.VariableValueField, OctopusImportPreviewChanges.FeedPasswordField }
            .ShouldBe(["ReleaseNotes", "State", "QueueTime", "StartTime", "CompletedTime", "ErrorMessage", "HasWarningsOrErrors", "Details", "ServerTask", "History", "Value", "Password"]);
    }

    private static OctopusServerTaskDto Task() => new() { Id = "ServerTasks-1", State = "Success", QueueTime = Queued, StartTime = Started, CompletedTime = Finished, ErrorMessage = null, HasWarningsOrErrors = false };

    private static OctopusImportStoredTask StoredTask() => new(TaskRow, TaskState.Success, Queued, Started, Finished, "", false);

    private static OctopusImportPriorMapping StoredUnit(string detailFingerprint = null) => new(70, "Created", "unit-fingerprint", detailFingerprint);

    private static OctopusImportStoredHistory Stored(OctopusImportStoredTask task) => new(new Dictionary<int, string>(), new Dictionary<int, OctopusImportStoredTask> { [task.Id] = task });

    private static void Set(OctopusServerTaskDto task, string field, string value)
    {
        switch (field)
        {
            case "State": task.State = value; break;
            case "QueueTime": task.QueueTime = DateTimeOffset.Parse(value); break;
            case "StartTime": task.StartTime = DateTimeOffset.Parse(value); break;
            case "CompletedTime": task.CompletedTime = value == null ? null : DateTimeOffset.Parse(value); break;
            case "ErrorMessage": task.ErrorMessage = value; break;
            case "HasWarningsOrErrors": task.HasWarningsOrErrors = bool.Parse(value); break;
        }
    }
}
