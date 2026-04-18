using System;
using System.IO;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Pure file-naming + on-disk-shape coverage for <see cref="LocalScriptService"/>'s
/// multi-syntax support. Verifies that adding Python / CSharp / FSharp to
/// <see cref="ScriptType"/> wires through to the right extension and encoding
/// without depending on whether <c>python3</c> / <c>dotnet-script</c> /
/// <c>dotnet fsi</c> are installed on the test host (those are exercised
/// separately by the E2E suite).
/// </summary>
public sealed class MultiSyntaxScriptDispatchTests
{
    [Theory]
    [InlineData(ScriptType.Bash, "script.sh")]
    [InlineData(ScriptType.PowerShell, "script.ps1")]
    [InlineData(ScriptType.Python, "script.py")]
    [InlineData(ScriptType.CSharp, "script.csx")]
    [InlineData(ScriptType.FSharp, "script.fsx")]
    public void ScriptFileNameFor_ReturnsCanonicalNamePerSyntax(ScriptType syntax, string expected)
    {
        LocalScriptService.ScriptFileNameFor(syntax).ShouldBe(expected);
    }

    [Fact]
    public void WriteScriptFile_PythonSyntax_WritesScriptDotPyWithoutBom()
    {
        var workDir = NewWorkDir();
        try
        {
            LocalScriptService.WriteScriptFile(workDir, "import os\nprint(os.getcwd())\n", ScriptType.Python);

            var path = Path.Combine(workDir, "script.py");
            File.Exists(path).ShouldBeTrue();

            var bytes = File.ReadAllBytes(path);
            // BOM = 0xEF 0xBB 0xBF — Python tolerates it but bash/dotnet-script
            // do NOT, and a stray BOM at the top of a script handed to the wrong
            // interpreter causes opaque syntax errors. Hard-assert no BOM.
            HasUtf8Bom(bytes).ShouldBeFalse("Python scripts must be BOM-less UTF-8");
        }
        finally { TryDelete(workDir); }
    }

    [Fact]
    public void WriteScriptFile_PowerShellSyntax_WritesScriptDotPs1WithBom()
    {
        var workDir = NewWorkDir();
        try
        {
            LocalScriptService.WriteScriptFile(workDir, "Write-Output 'hi'", ScriptType.PowerShell);

            var path = Path.Combine(workDir, "script.ps1");
            File.Exists(path).ShouldBeTrue();

            var bytes = File.ReadAllBytes(path);
            // .ps1 needs the BOM so Windows PowerShell 5.1 parses non-ASCII.
            HasUtf8Bom(bytes).ShouldBeTrue("PowerShell scripts must carry the UTF-8 BOM for WinPS 5.1 compatibility");
        }
        finally { TryDelete(workDir); }
    }

    [Theory]
    [InlineData(ScriptType.Bash, "script.sh")]
    [InlineData(ScriptType.CSharp, "script.csx")]
    [InlineData(ScriptType.FSharp, "script.fsx")]
    public void WriteScriptFile_NonPowerShellSyntaxes_AreBomLessUtf8(ScriptType syntax, string expectedFile)
    {
        var workDir = NewWorkDir();
        try
        {
            LocalScriptService.WriteScriptFile(workDir, "// some content", syntax);

            var path = Path.Combine(workDir, expectedFile);
            File.Exists(path).ShouldBeTrue();
            HasUtf8Bom(File.ReadAllBytes(path)).ShouldBeFalse();
        }
        finally { TryDelete(workDir); }
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }
}
