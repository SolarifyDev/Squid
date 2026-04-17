using Shouldly;
using Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Observability;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Observability;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class ExecutionManifestTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-manifest-test-{Guid.NewGuid():N}");

    public ExecutionManifestTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Build_HashesScriptBody_DeterministicAcrossInvocations()
    {
        var command = MakeCommand("echo hello");
        var m1 = ExecutionManifest.Build("t", command, "1.0", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow);
        var m2 = ExecutionManifest.Build("t", command, "1.0", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow);

        m1.ScriptBodyHash.ShouldStartWith("sha256:");
        m1.ScriptBodyHash.ShouldBe(m2.ScriptBodyHash);
    }

    [Fact]
    public void Build_DifferentScriptBody_ProducesDifferentHash()
    {
        var m1 = ExecutionManifest.Build("t", MakeCommand("echo a"), "1.0", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow);
        var m2 = ExecutionManifest.Build("t", MakeCommand("echo b"), "1.0", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow);

        m1.ScriptBodyHash.ShouldNotBe(m2.ScriptBodyHash);
    }

    [Fact]
    public void Build_WithWorkspace_HashesFilesFromDisk()
    {
        File.WriteAllText(Path.Combine(_workspace, "deployment.yaml"), "apiVersion: v1\nkind: Pod");

        var command = MakeCommand("echo deploy", fileNames: new[] { "deployment.yaml" });
        var manifest = ExecutionManifest.Build("t", command, "1.0", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, workspace: _workspace);

        manifest.FileHashes.ShouldContainKey("deployment.yaml");
        manifest.FileHashes["deployment.yaml"].ShouldStartWith("sha256:");
        manifest.FileHashes["deployment.yaml"].ShouldNotBe("sha256:missing");
        manifest.FileHashes["deployment.yaml"].ShouldNotBe("sha256:error");
    }

    [Fact]
    public void Build_FileNotOnDisk_RecordsMissingMarker()
    {
        var command = MakeCommand("echo x", fileNames: new[] { "never-written.yaml" });
        var manifest = ExecutionManifest.Build("t", command, "1.0", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, workspace: _workspace);

        manifest.FileHashes["never-written.yaml"].ShouldBe("sha256:missing");
    }

    [Fact]
    public void WriteTo_ThenTryRead_RoundTrips()
    {
        var command = MakeCommand("sleep 1");
        var original = ExecutionManifest.Build("ticket-123", command, "2.0", DateTimeOffset.Parse("2026-04-17T12:00:00Z"), exitCode: 0, completedAt: DateTimeOffset.Parse("2026-04-17T12:00:05Z"), traceId: "abc123");

        original.WriteTo(_workspace);
        var loaded = ExecutionManifest.TryRead(_workspace);

        loaded.ShouldNotBeNull();
        loaded!.TicketId.ShouldBe("ticket-123");
        loaded.AgentVersion.ShouldBe("2.0");
        loaded.ExitCode.ShouldBe(0);
        loaded.TraceId.ShouldBe("abc123");
    }

    [Fact]
    public void TryRead_NoFile_ReturnsNull()
    {
        ExecutionManifest.TryRead(_workspace).ShouldBeNull();
    }

    [Fact]
    public void HashText_EmptyInput_ProducesStableHash()
    {
        var h = ExecutionManifest.HashText("");
        h.ShouldBe("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    private static StartScriptCommand MakeCommand(string scriptBody, string[]? fileNames = null)
    {
        var files = fileNames?.Select(n => new ScriptFile(n, DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("payload-" + n)))).ToArray() ?? Array.Empty<ScriptFile>();

        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero,
            files);
    }
}
