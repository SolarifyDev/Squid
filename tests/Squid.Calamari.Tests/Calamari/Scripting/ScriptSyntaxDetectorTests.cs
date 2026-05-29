using Shouldly;
using Squid.Calamari.Scripting;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Scripting;

/// <summary>
/// PR-4 — extension → syntax mapping. Operator-facing contract: name the
/// script <c>.ps1</c> for PowerShell, anything else (including .sh, .bash,
/// or no extension) for bash. The detector MUST NOT introduce a new wire
/// literal — the extension itself is the hint.
/// </summary>
public sealed class ScriptSyntaxDetectorTests
{
    [Theory]
    [InlineData("/x/y.ps1", ScriptSyntax.PowerShell)]
    [InlineData("/x/y.PS1", ScriptSyntax.PowerShell)]
    [InlineData("/x/y.psm1", ScriptSyntax.PowerShell)]
    [InlineData("/x/y.py", ScriptSyntax.Python)]
    [InlineData("/x/y.PY", ScriptSyntax.Python)]
    [InlineData("/x/y.sh", ScriptSyntax.Bash)]
    [InlineData("/x/y.bash", ScriptSyntax.Bash)]
    [InlineData("/x/y", ScriptSyntax.Bash)]    // no extension → bash default
    [InlineData("/x/y.unknown", ScriptSyntax.Bash)]
    public void DetectFromPath_MapsExtensionToSyntax(string path, ScriptSyntax expected)
        => ScriptSyntaxDetector.DetectFromPath(path).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectFromPath_NullOrEmpty_DefaultsToBash(string? path)
        => ScriptSyntaxDetector.DetectFromPath(path!).ShouldBe(ScriptSyntax.Bash,
            customMessage: "Empty / null script path MUST fall back to Bash — preserves existing operator behaviour " +
                           "for callers that haven't set ScriptPath yet (defensive default).");
}
