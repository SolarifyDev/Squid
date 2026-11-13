using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportHistoryWriteRulesTests
{
    private static readonly OctopusServerTaskDto Task = new() { Id = "ServerTasks-1", State = "Success", CompletedTime = new DateTimeOffset(2024, 7, 10, 9, 48, 11, TimeSpan.Zero) };

    private static string TaskFingerprint => OctopusImportFingerprint.ForTask(Task);

    [Theory]
    [InlineData(true, true, false)]    // same task, row present → unchanged
    [InlineData(false, true, true)]    // task changed in Octopus
    [InlineData(true, false, true)]    // task row gone → re-created
    public void ScalarChanged_Theory(bool sameFingerprint, bool taskRowExists, bool expected)
    {
        var stored = new OctopusImportPriorMapping(10, "Created", sameFingerprint ? TaskFingerprint : "other", null);

        OctopusImportHistoryWriteRules.ScalarChanged(stored, Task, taskRowExists).ShouldBe(expected);
    }

    [Fact]
    public void ScalarChanged_BackfilledRowWithoutFingerprint_IsChanged()
        => OctopusImportHistoryWriteRules.ScalarChanged(new OctopusImportPriorMapping(10, "Created", null, null), Task, true).ShouldBeTrue();

    [Theory]
    [InlineData("stored", "incoming", false, "stored", true)]   // new details differ → replace
    [InlineData("stored", "stored", true, "stored", false)]     // same details, even with changed scalars → keep
    [InlineData("stored", null, true, "stored", false)]         // no incoming details never replaces stored details (monotonic)
    [InlineData("stored", null, true, null, true)]              // summary-level unit whose task changed → refresh the summary
    [InlineData("stored", null, false, null, false)]            // summary-level unit, nothing changed → keep
    [InlineData(null, null, false, null, true)]                 // backfilled #542 row (no fingerprint) → normalised once
    public void ReplaceChildren_Theory(string storedFingerprint, string incomingDetails, bool scalarChanged, string storedDetails, bool expected)
    {
        var stored = new OctopusImportPriorMapping(10, "Created", storedFingerprint, storedDetails);

        OctopusImportHistoryWriteRules.ReplaceChildren(stored, incomingDetails, scalarChanged, taskRowExists: true).ShouldBe(expected);
    }

    [Fact]
    public void ReplaceChildren_TaskRowGone_AlwaysWrites()
        => OctopusImportHistoryWriteRules.ReplaceChildren(new OctopusImportPriorMapping(10, "Created", "fp", "details"), null, false, taskRowExists: false).ShouldBeTrue();
}
