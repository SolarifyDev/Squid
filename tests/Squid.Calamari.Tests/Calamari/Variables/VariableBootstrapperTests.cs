using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Variables;

/// <summary>
/// P1-Phase-7 audit follow-up to B.6: Calamari's <c>VariableBootstrapper</c>
/// is the AGENT-SIDE bash export preamble generator (the server-side
/// counterpart is <c>BashRuntimeBundle</c> in Squid.Core). B.6 migrated
/// the server side to single-quote wrapping; this file pins the matching
/// migration on the agent side, closing the same shell-injection class
/// (newline / metacharacter escape) on the parallel code path.
///
/// <para>New contract: every value is wrapped in single quotes; every
/// metacharacter inside the quote is literal; only <c>'</c> itself needs
/// the four-character POSIX idiom <c>'\''</c>. Newlines, tabs, $, `, ",
/// \\, !, # all preserved verbatim — no lossy text-replacement.</para>
/// </summary>
public class VariableBootstrapperTests
{
    [Fact]
    public void GeneratePreamble_ValidVariable_ExportsCorrectly()
    {
        var vars = new Dictionary<string, string> { ["APP_NAME"] = "squid" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain("export APP_NAME='squid'");
    }

    [Theory]
    [InlineData("Squid.Action[Deploy].Name")]
    [InlineData("Squid.Step[1].Status")]
    [InlineData("var[0]")]
    public void GeneratePreamble_VariableWithBrackets_IsSkipped(string name)
    {
        var vars = new Dictionary<string, string> { [name] = "value" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldNotContain("export");
        preamble.ShouldNotContain("value");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void GeneratePreamble_EmptyOrNullName_IsSkipped(string name)
    {
        var vars = new List<KeyValuePair<string, string>> { new(name ?? "", "value") };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldNotContain("export");
    }

    [Fact]
    public void GeneratePreamble_NameStartingWithDigit_IsSkipped()
    {
        var vars = new Dictionary<string, string> { ["0invalid"] = "value" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldNotContain("export");
    }

    [Theory]
    [InlineData("Squid.Action.Name", "Squid_Action_Name")]
    [InlineData("my-var", "my_var")]
    [InlineData("path/to/var", "path_to_var")]
    public void GeneratePreamble_SanitizesDotsHyphenSlashes(string name, string expectedEnvName)
    {
        var vars = new Dictionary<string, string> { [name] = "test" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain($"export {expectedEnvName}='test'");
    }

    [Theory]
    [InlineData("hello\"world", "'hello\"world'")]   // " is literal inside single quotes
    [InlineData("price$5", "'price$5'")]              // $ is literal
    [InlineData("back\\slash", "'back\\slash'")]      // \ is literal
    [InlineData("tick`cmd`", "'tick`cmd`'")]          // ` is literal
    [InlineData("has !bang", "'has !bang'")]          // ! is literal
    [InlineData("has #hash", "'has #hash'")]          // # is literal
    public void GeneratePreamble_ShellMetachars_AllLiteralViaSingleQuoting(string value, string expectedQuoted)
    {
        var vars = new Dictionary<string, string> { ["VAR"] = value };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain($"export VAR={expectedQuoted}");
    }

    [Theory]
    [InlineData("it's", "'it'\\''s'")]              // POSIX idiom for embedded '
    [InlineData("'", "''\\'''")]
    [InlineData("a'b'c", "'a'\\''b'\\''c'")]
    public void GeneratePreamble_EmbeddedSingleQuote_UsesPosixIdiom(string value, string expectedQuoted)
    {
        var vars = new Dictionary<string, string> { ["VAR"] = value };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain($"export VAR={expectedQuoted}");
    }

    [Fact]
    public void GeneratePreamble_NewlineInValue_PreservedNotMangled_NoInjection()
    {
        // The actual security regression test: pre-fix this replaced \n
        // with literal "\\n" two-char text — safe but LOSSY, the operator's
        // newline value was mangled. The new single-quote wrapping
        // PRESERVES the real newline character inside the quoted literal,
        // so the variable value round-trips intact AND can't escape the
        // export statement (the closing single-quote is on the line AFTER
        // the embedded newline, so bash treats the whole multi-line span
        // as one literal word).
        var malicious = "value\nrm -rf /tmp/squid-pwned";
        var vars = new Dictionary<string, string> { ["VAR"] = malicious };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        // The quoted form contains the literal newline byte.
        preamble.ShouldContain("export VAR='value\nrm -rf /tmp/squid-pwned'");

        // And the malicious payload is INSIDE the quotes, never standalone.
        var lines = preamble.Split('\n');
        lines.Any(l => l.Trim() == "rm -rf /tmp/squid-pwned").ShouldBeFalse(
            customMessage: "newline-injection regression — the payload escaped the quoted span!");
    }

    [Fact]
    public void GeneratePreamble_MixedValidAndInvalid_OnlyExportsValid()
    {
        var vars = new Dictionary<string, string>
        {
            ["VALID_VAR"] = "good",
            ["Squid.Action[0].Name"] = "bad",
            ["another_valid"] = "also_good"
        };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain("export VALID_VAR='good'");
        preamble.ShouldContain("export another_valid='also_good'");
        preamble.ShouldNotContain("[0]");
    }
}
