using System;
using System.Collections.Generic;
using System.IO;
using Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class WorkspaceSecurityTests : IDisposable
{
    private readonly string _workDir;

    public WorkspaceSecurityTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"squid-ws-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    [Fact]
    public void WriteAdditionalFiles_NormalFile_Written()
    {
        var files = new List<ScriptFile>
        {
            new("script.sh", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("echo hello")))
        };

        ScriptPodService.WriteAdditionalFiles(_workDir, files);

        File.Exists(Path.Combine(_workDir, "script.sh")).ShouldBeTrue();
    }

    [Fact]
    public void WriteAdditionalFiles_RelativeSubdir_Allowed()
    {
        var files = new List<ScriptFile>
        {
            new("subdir/config.yaml", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("key: value")))
        };

        ScriptPodService.WriteAdditionalFiles(_workDir, files);

        File.Exists(Path.Combine(_workDir, "subdir", "config.yaml")).ShouldBeTrue();
    }

    [Fact]
    public void WriteAdditionalFiles_PathTraversal_Skipped()
    {
        var files = new List<ScriptFile>
        {
            new("../../etc/passwd", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("evil")))
        };

        ScriptPodService.WriteAdditionalFiles(_workDir, files);

        File.Exists(Path.Combine(_workDir, "../../etc/passwd")).ShouldBeFalse();
    }

    [Fact]
    public void WriteAdditionalFiles_AbsolutePath_Skipped()
    {
        var files = new List<ScriptFile>
        {
            new("/tmp/evil", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("evil")))
        };

        ScriptPodService.WriteAdditionalFiles(_workDir, files);

        File.Exists("/tmp/evil").ShouldBeFalse();
    }

    [Fact]
    public void WriteAdditionalFiles_DotDotInSubdir_Skipped()
    {
        var files = new List<ScriptFile>
        {
            new("foo/../../../etc/passwd", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("evil")))
        };

        ScriptPodService.WriteAdditionalFiles(_workDir, files);

        var traversedPath = Path.GetFullPath(Path.Combine(_workDir, "foo/../../../etc/passwd"));
        File.Exists(traversedPath).ShouldBeFalse();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
                Directory.Delete(_workDir, true);
        }
        catch { }
    }
}
