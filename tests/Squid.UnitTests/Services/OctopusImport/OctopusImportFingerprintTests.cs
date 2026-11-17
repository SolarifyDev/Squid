using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportFingerprintTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly DateTimeOffset Queued = new(2024, 3, 1, 10, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Release_OnlyNotes()
    {
        var release = new OctopusReleaseDto { Id = "Releases-1", Version = "1.0.0", ReleaseNotes = "Fixes", SelectedPackages = [new OctopusSelectedPackageDto { ActionName = "Deploy", Version = "1.0.0" }] };
        var otherVersionAndPackages = new OctopusReleaseDto { Id = "Releases-1", Version = "9.9.9", ReleaseNotes = "  Fixes  ", SelectedPackages = [] };

        OctopusImportFingerprint.ForRelease(otherVersionAndPackages).ShouldBe(OctopusImportFingerprint.ForRelease(release));
        OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = "Other" }).ShouldNotBe(OctopusImportFingerprint.ForRelease(release));
        OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = null }).ShouldBe(OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = "" }));
        OctopusImportFingerprint.ForRelease(null).ShouldBeNull();
    }

    [Fact]
    public void Task_CanonicalizesTimesAndNulls()
    {
        var utc = Task(Queued, errorMessage: null);
        var sameInstantOtherOffsetAndSubMillisecond = Task(Queued.ToOffset(TimeSpan.FromHours(10)).AddTicks(9999), errorMessage: "  ");

        OctopusImportFingerprint.ForTask(sameInstantOtherOffsetAndSubMillisecond).ShouldBe(OctopusImportFingerprint.ForTask(utc));
        OctopusImportFingerprint.ForTask(Task(Queued.AddMilliseconds(1), errorMessage: null)).ShouldNotBe(OctopusImportFingerprint.ForTask(utc));
        OctopusImportFingerprint.ForTask(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(nameof(OctopusServerTaskDto.State))]
    [InlineData(nameof(OctopusServerTaskDto.StartTime))]
    [InlineData(nameof(OctopusServerTaskDto.CompletedTime))]
    [InlineData(nameof(OctopusServerTaskDto.ErrorMessage))]
    [InlineData(nameof(OctopusServerTaskDto.HasWarningsOrErrors))]
    public void Task_EveryWrittenScalar_ChangesTheHash(string field)
    {
        var original = Task(Queued, errorMessage: null);
        var changed = Task(Queued, errorMessage: null);

        switch (field)
        {
            case nameof(OctopusServerTaskDto.State): changed.State = "Failed"; break;
            case nameof(OctopusServerTaskDto.StartTime): changed.StartTime = changed.StartTime!.Value.AddSeconds(1); break;
            case nameof(OctopusServerTaskDto.CompletedTime): changed.CompletedTime = null; break;
            case nameof(OctopusServerTaskDto.ErrorMessage): changed.ErrorMessage = "boom"; break;
            case nameof(OctopusServerTaskDto.HasWarningsOrErrors): changed.HasWarningsOrErrors = true; break;
        }

        OctopusImportFingerprint.ForTask(changed).ShouldNotBe(OctopusImportFingerprint.ForTask(original));
    }

    [Fact]
    public void Task_DurationSecondsAbsentFromTheApiShape_DoesNotChangeTheHash()
    {
        var export = Task(Queued, errorMessage: null);
        var api = Task(Queued, errorMessage: null);
        api.DurationSeconds = 0;

        OctopusImportFingerprint.ForTask(api).ShouldBe(OctopusImportFingerprint.ForTask(export));
    }

    [Theory]
    [InlineData(
        """{"Id":"ServerTasks-1","Name":"Deploy","State":"Success","QueueTime":"2024-03-01T10:00:00.1234567+00:00","StartTime":"2024-03-01T10:00:01.5000000+00:00","CompletedTime":"2024-03-01T10:00:05.9999999+00:00","ErrorMessage":null,"HasWarningsOrErrors":false,"DurationSeconds":4,"ServerNode":"node-1","JSON":"{}"}""",
        """{"Id":"ServerTasks-1","Name":"Deploy","State":"Success","QueueTime":"2024-03-01T20:00:00.123+10:00","StartTime":"2024-03-01T20:00:01.500+10:00","CompletedTime":"2024-03-01T20:00:05.999+10:00","ErrorMessage":"","HasWarningsOrErrors":false,"Duration":"4 seconds","IsCompleted":true,"Links":{"Self":"/api/tasks/ServerTasks-1"}}""")]
    [InlineData(
        """{"Id":"ServerTasks-2","State":"Failed","QueueTime":"2024-03-01T10:00:00Z","StartTime":"2024-03-01T10:00:01Z","CompletedTime":"2024-03-01T10:01:00Z","ErrorMessage":"Step failed","HasWarningsOrErrors":true,"DurationSeconds":59}""",
        """{"Id":"ServerTasks-2","State":"Failed","QueueTime":"2024-03-01T05:00:00.000-05:00","StartTime":"2024-03-01T05:00:01.000-05:00","CompletedTime":"2024-03-01T05:01:00.000-05:00","ErrorMessage":" Step failed ","HasWarningsOrErrors":true,"Duration":"59 seconds"}""")]
    public void Task_ExportAndApiShapes_SameHash(string exportJson, string apiJson)
    {
        var export = JsonSerializer.Deserialize<OctopusServerTaskDto>(exportJson, JsonOptions);
        var api = JsonSerializer.Deserialize<OctopusServerTaskDto>(apiJson, JsonOptions);

        OctopusImportFingerprint.ForTask(api).ShouldBe(OctopusImportFingerprint.ForTask(export));
    }

    [Fact]
    public void Details_NullWhenAbsent()
    {
        OctopusImportFingerprint.ForTaskDetails(null).ShouldBeNull();
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails()).ShouldBeNull();
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = null, Interruptions = null, Events = null, Artifacts = null }).ShouldBeNull();
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { Artifacts = [new OctopusArtifactDto { Id = "Artifacts-1", Filename = "out.txt" }] }).ShouldNotBeNull();
    }

    [Fact]
    public void Details_PreThreeFourSingleActivityLog_HashesLikeTheListShape()
    {
        var single = new OctopusTaskHistoryDetails { ActivityLog = Tree("Success") };
        var list = new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success")] };

        OctopusImportFingerprint.ForTaskDetails(single).ShouldBe(OctopusImportFingerprint.ForTaskDetails(list));
    }

    [Fact]
    public void Details_TreeStatusAndLogLines_ChangeTheHash()
    {
        var original = OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success")] });

        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Failed")] }).ShouldNotBe(original);
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success", "line one", "line TWO")] }).ShouldNotBe(original);
    }

    [Fact]
    public void Details_LogLinesPastTheCap_AreNotHashedButTheirCountIs()
    {
        var capped = OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success", "a", "b", "c")] }, maxLogLines: 2);

        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success", "a", "b", "changed")] }, maxLogLines: 2).ShouldBe(capped);
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success", "a", "b", "c", "d")] }, maxLogLines: 2).ShouldNotBe(capped);
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { ActivityLogs = [Tree("Success", "a", "b", "c")] }).ShouldNotBe(capped);
    }

    [Fact]
    public void Details_UnorderedCollections_AreSortedBeforeHashing()
    {
        var first = new OctopusEventDto { Id = "Events-1", Category = "DeploymentQueued", Message = "queued" };
        var second = new OctopusEventDto { Id = "Events-2", Category = "DeploymentSucceeded", Message = "done" };
        var interruption = new OctopusInterruptionDto { Id = "Interruptions-1", ResponsibleTeamIds = ["Teams-2", "Teams-1"], Form = new OctopusInterruptionFormDto { Values = new() { ["Result"] = "Proceed", ["Notes"] = "ok" } } };
        var reordered = new OctopusInterruptionDto { Id = "Interruptions-1", ResponsibleTeamIds = ["Teams-1", "Teams-2"], Form = new OctopusInterruptionFormDto { Values = new() { ["Notes"] = "ok", ["Result"] = "Proceed" } } };

        var hash = OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { Events = [first, second], Interruptions = [interruption] });

        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { Events = [second, first], Interruptions = [reordered] }).ShouldBe(hash);
        OctopusImportFingerprint.ForTaskDetails(new OctopusTaskHistoryDetails { Events = [first], Interruptions = [interruption] }).ShouldNotBe(hash);
    }

    [Fact]
    public void Config_UsesDataVersionThenLastModified()
    {
        var modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        OctopusImportFingerprint.ForConfig(new OctopusProjectDto { DataVersion = "AAA=", LastModified = modified }).ShouldBe(OctopusImportFingerprint.ForConfig(new OctopusProjectDto { DataVersion = "AAA=" }));
        OctopusImportFingerprint.ForConfig(new OctopusProjectDto { LastModified = modified }).ShouldNotBeNull();
        OctopusImportFingerprint.ForConfig(new OctopusProjectDto { LastModified = modified }).ShouldNotBe(OctopusImportFingerprint.ForConfig(new OctopusProjectDto { LastModified = modified.AddSeconds(1) }));
        OctopusImportFingerprint.ForConfig(new OctopusProjectDto()).ShouldBeNull();
        OctopusImportFingerprint.ForConfig(null).ShouldBeNull();
    }

    [Fact]
    public void VersionPrefix()
    {
        // Stored fingerprints are compared across releases; changing the prefix invalidates every stored row on purpose.
        OctopusImportFingerprint.VersionPrefix.ShouldBe("v1|");

        OctopusImportFingerprint.Digest("payload").ShouldBe(Sha256("v1|payload"));
        OctopusImportFingerprint.Digest("payload").ShouldNotBe(Sha256("payload"));

        var hash = OctopusImportFingerprint.ForTask(Task(Queued, errorMessage: null));
        hash.Length.ShouldBe(64);
        hash.ShouldBe(hash.ToLowerInvariant());
    }

    [Fact]
    public void DefaultMaxLogLinesPerTask_Pinned()
    {
        OctopusImportFingerprint.DefaultMaxLogLinesPerTask.ShouldBe(20000);
    }

    private static OctopusServerTaskDto Task(DateTimeOffset queued, string errorMessage)
        => new() { Id = "ServerTasks-1", State = "Success", QueueTime = queued, StartTime = queued.AddSeconds(1), CompletedTime = queued.AddSeconds(5), ErrorMessage = errorMessage, HasWarningsOrErrors = false, DurationSeconds = 4 };

    private static OctopusActivityLogDto Tree(string status, params string[] lines)
        => new()
        {
            Id = "ServerTasks-1",
            Name = "Deploy",
            Status = status,
            Children = [new OctopusActivityLogDto { Id = "ServerTasks-1_step", Name = "Step 1", Status = status, LogElements = lines.Select(l => new OctopusLogElementDto { Category = "Info", MessageText = l }).ToList() }]
        };

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
