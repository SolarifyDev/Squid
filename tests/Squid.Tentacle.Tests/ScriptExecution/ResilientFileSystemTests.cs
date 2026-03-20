using System;
using System.IO;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class ResilientFileSystemTests : IDisposable
{
    private readonly string _tempDir;

    public ResilientFileSystemTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void WriteAllText_WritesContent()
    {
        var path = Path.Combine(_tempDir, "test.txt");

        ResilientFileSystem.WriteAllText(path, "hello");

        File.ReadAllText(path).ShouldBe("hello");
    }

    [Fact]
    public void ReadAllText_ReadsContent()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "world");

        ResilientFileSystem.ReadAllText(path).ShouldBe("world");
    }

    [Fact]
    public void Move_MovesFile()
    {
        var src = Path.Combine(_tempDir, "src.txt");
        var dst = Path.Combine(_tempDir, "dst.txt");
        File.WriteAllText(src, "data");

        ResilientFileSystem.Move(src, dst, overwrite: true);

        File.Exists(src).ShouldBeFalse();
        File.ReadAllText(dst).ShouldBe("data");
    }

    [Fact]
    public void DeleteFile_DeletesFile()
    {
        var path = Path.Combine(_tempDir, "delete-me.txt");
        File.WriteAllText(path, "gone");

        ResilientFileSystem.DeleteFile(path);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void FileExists_ReturnsTrueForExistingFile()
    {
        var path = Path.Combine(_tempDir, "exists.txt");
        File.WriteAllText(path, "yes");

        ResilientFileSystem.FileExists(path).ShouldBeTrue();
    }

    [Fact]
    public void FileExists_ReturnsFalseForMissingFile()
    {
        ResilientFileSystem.FileExists(Path.Combine(_tempDir, "nope.txt")).ShouldBeFalse();
    }

    [Fact]
    public void CreateDirectory_CreatesDirectory()
    {
        var path = Path.Combine(_tempDir, "subdir");

        ResilientFileSystem.CreateDirectory(path);

        Directory.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void DirectoryExists_ReturnsTrueForExistingDirectory()
    {
        ResilientFileSystem.DirectoryExists(_tempDir).ShouldBeTrue();
    }

    [Fact]
    public void DirectoryExists_ReturnsFalseForMissingDirectory()
    {
        ResilientFileSystem.DirectoryExists(Path.Combine(_tempDir, "missing")).ShouldBeFalse();
    }

    [Fact]
    public void DeleteDirectory_DeletesDirectory()
    {
        var sub = Path.Combine(_tempDir, "to-delete");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "file.txt"), "data");

        ResilientFileSystem.DeleteDirectory(sub, recursive: true);

        Directory.Exists(sub).ShouldBeFalse();
    }

    [Fact]
    public void GetDirectories_ListsSubdirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "a"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "b"));

        var dirs = ResilientFileSystem.GetDirectories(_tempDir);

        dirs.Length.ShouldBe(2);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 400)]
    [InlineData(3, 900)]
    [InlineData(5, 2500)]
    [InlineData(10, 10000)]
    public void CalculateDelay_QuadraticBackoff(int attempt, int expectedMs)
    {
        ResilientFileSystem.CalculateDelay(attempt).ShouldBe(expectedMs);
    }

    [Fact]
    public void ReadAllText_FileNotFound_ThrowsImmediately()
    {
        Should.Throw<FileNotFoundException>(() =>
            ResilientFileSystem.ReadAllText(Path.Combine(_tempDir, "no-such-file.txt")));
    }

    [Fact]
    public void DeleteDirectory_DirectoryNotFound_ThrowsImmediately()
    {
        Should.Throw<DirectoryNotFoundException>(() =>
            ResilientFileSystem.DeleteDirectory(Path.Combine(_tempDir, "no-such-dir"), recursive: true));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
