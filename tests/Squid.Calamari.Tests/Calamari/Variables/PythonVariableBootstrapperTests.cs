using Shouldly;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Variables;

/// <summary>
/// PR-10 — Python preamble shape. Mirrors the bash + PS bootstrapper test
/// suites: os.environ assignment, name sanitiser parity, lossless value
/// escaping (quotes / backslashes / newlines).
/// </summary>
public sealed class PythonVariableBootstrapperTests
{
    [Fact]
    public void GeneratePreamble_ImportsOsAndAssignsEnviron()
    {
        var preamble = PythonVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["MyVar"] = "hello" });

        preamble.ShouldContain("import os");
        preamble.ShouldContain("os.environ['MyVar'] = 'hello'",
            customMessage: "Python bootstrap MUST set os.environ so the operator script reads via os.getenv / os.environ.");
    }

    [Fact]
    public void GeneratePreamble_SanitisesDotsHyphensSlashes_SameAsBashAndPs()
    {
        var preamble = PythonVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string>
            {
                ["My.Dotted.Name"] = "v1",
                ["My-Hyphen-Name"] = "v2",
                ["My/Slash/Name"] = "v3"
            });

        preamble.ShouldContain("os.environ['My_Dotted_Name'] = 'v1'");
        preamble.ShouldContain("os.environ['My_Hyphen_Name'] = 'v2'");
        preamble.ShouldContain("os.environ['My_Slash_Name'] = 'v3'");
    }

    [Fact]
    public void GeneratePreamble_SkipsNamesStartingWithDigit()
    {
        var preamble = PythonVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["1stVar"] = "skipped", ["ValidVar"] = "kept" });

        preamble.ShouldNotContain("1stVar", Case.Insensitive);
        preamble.ShouldContain("ValidVar");
    }

    [Fact]
    public void GeneratePreamble_EscapesSingleQuoteAndBackslash()
    {
        var preamble = PythonVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["Path"] = @"C:\it's\here" });

        // Backslash → \\, single quote → \'. Python re-expands to C:\it's\here.
        preamble.ShouldContain(@"os.environ['Path'] = 'C:\\it\'s\\here'",
            customMessage: "Backslash + single-quote MUST be escaped for the Python single-quoted literal, losslessly.");
    }

    [Fact]
    public void GeneratePreamble_EscapesNewlinesAsBackslashN()
    {
        // PEM-key-style multi-line value. Python interprets \n in a single-
        // quoted literal as a real newline, so this round-trips losslessly.
        var pem = "-----BEGIN-----\nline1\nline2\n-----END-----";
        var preamble = PythonVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["Key"] = pem });

        preamble.ShouldContain(@"os.environ['Key'] = '-----BEGIN-----\nline1\nline2\n-----END-----'",
            customMessage: "Embedded newlines MUST become \\n escapes — single-line literal that Python expands back to the multi-line value.");
        // The raw preamble line MUST NOT contain a literal newline inside the
        // value (that would break the single-quoted string).
        preamble.ShouldNotContain("'-----BEGIN-----\n");
    }

    [Fact]
    public void GeneratePreamble_EscapesTabAndCarriageReturn()
    {
        var preamble = PythonVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["V"] = "a\tb\rc" });

        preamble.ShouldContain(@"os.environ['V'] = 'a\tb\rc'");
    }

    [Fact]
    public void GeneratePreamble_EmptyVariableSet_StillImportsOs()
    {
        var preamble = PythonVariableBootstrapper.GeneratePreamble(new Dictionary<string, string>());

        preamble.ShouldContain("import os");
        preamble.ShouldNotContain("os.environ[");
    }
}
