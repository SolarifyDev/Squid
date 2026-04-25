using System.Linq;
using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Runtime.Bundles;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Runtime.Bundles;

public class BashRuntimeBundleTests
{
    private static RuntimeBundleWrapContext MakeContext(
        string script = "echo hello",
        int serverTaskId = 42,
        string workDir = "/home/user/.squid/Work/42",
        string baseDir = "/home/user/.squid",
        IReadOnlyList<VariableDto> variables = null)
    {
        return new RuntimeBundleWrapContext
        {
            UserScriptBody = script,
            WorkDirectory = workDir,
            BaseDirectory = baseDir,
            ServerTaskId = serverTaskId,
            Variables = variables ?? Array.Empty<VariableDto>()
        };
    }

    // ========== Kind ==========

    [Fact]
    public void Kind_IsBash()
    {
        var bundle = new BashRuntimeBundle();

        bundle.Kind.ShouldBe(RuntimeBundleKind.Bash);
    }

    // ========== Header ==========

    [Fact]
    public void Wrap_FirstLineIsBashShebang()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext());

        result.ShouldStartWith("#!/bin/bash\n");
    }

    // ========== Squid scope variables ==========

    [Fact]
    public void Wrap_ExportsSquidHome()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(baseDir: "/home/deploy/.squid"));

        result.ShouldContain("export SquidHome='/home/deploy/.squid'");
    }

    [Fact]
    public void Wrap_ExportsSquidWorkDirectory()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(workDir: "/home/deploy/.squid/Work/99"));

        result.ShouldContain("export SquidWorkDirectory='/home/deploy/.squid/Work/99'");
    }

    [Fact]
    public void Wrap_ExportsSquidServerTaskId()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(serverTaskId: 1234));

        // ServerTaskId is an int, so the value is always a clean integer literal —
        // single-quote wrapping is harmless and consistent with the other exports.
        result.ShouldContain("export SquidServerTaskId='1234'");
    }

    // ========== Deployment variable exports ==========

    [Fact]
    public void Wrap_ExportsNonSensitiveVariables()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Database.Host", Value = "db.example.com", IsSensitive = false },
            new() { Name = "App.Env", Value = "production", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export Database_Host='db.example.com'");
        result.ShouldContain("export App_Env='production'");
    }

    [Fact]
    public void Wrap_SkipsSensitiveVariables()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Api.Key", Value = "super-secret", IsSensitive = true },
            new() { Name = "App.Env", Value = "production", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export App_Env='production'");
        result.ShouldNotContain("super-secret");
        result.ShouldNotContain("export Api_Key=");
    }

    [Fact]
    public void Wrap_SkipsVariablesWithEmptyName()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "", Value = "orphan", IsSensitive = false },
            new() { Name = "Valid", Value = "ok", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export Valid='ok'");
        result.ShouldNotContain("export ='orphan'");
    }

    [Fact]
    public void Wrap_SanitizesVariableNamesWithLeadingDigit()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "1stReplicaCount", Value = "3", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export _1stReplicaCount='3'");
    }

    [Fact]
    public void Wrap_VariableValueWithMetacharacters_AllLiteralViaSingleQuoting()
    {
        // Single-quote wrapping makes ALL these characters literal — no
        // backslash escaping needed. Bash sees the entire span between the
        // single quotes as one literal word.
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "ConnStr", Value = "host=$HOST;pwd=\"secret\";hook=`whoami`", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export ConnStr='host=$HOST;pwd=\"secret\";hook=`whoami`'");
    }

    // ========== Helper function injection ==========

    [Fact]
    public void Wrap_IncludesEmbeddedHelperFunctions()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext());

        result.ShouldContain("set_squidvariable()");
        result.ShouldContain("new_squidartifact()");
        result.ShouldContain("fail_step()");
        result.ShouldContain("get_squidvariable()");
    }

    [Fact]
    public void Wrap_HelperResourceIsLoadedNonEmpty()
    {
        BashRuntimeBundle.Helpers.ShouldNotBeNullOrWhiteSpace();
        BashRuntimeBundle.Helpers.ShouldContain("##squid[setVariable");
    }

    // ========== User script placement ==========

    [Fact]
    public void Wrap_AppendsUserScriptAfterHelpers()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(script: "echo 'user script body here'"));

        var helperEnd = result.IndexOf("# --- end squid-runtime.sh ---", StringComparison.Ordinal);
        var userIndex = result.IndexOf("echo 'user script body here'", StringComparison.Ordinal);
        helperEnd.ShouldBeGreaterThan(0);
        userIndex.ShouldBeGreaterThan(helperEnd);
    }

    [Fact]
    public void Wrap_NullUserScript_StillReturnsWrapperOnly()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(script: null));

        result.ShouldStartWith("#!/bin/bash\n");
        result.ShouldContain("set_squidvariable()");
    }

    [Fact]
    public void Wrap_NullContext_Throws()
    {
        var bundle = new BashRuntimeBundle();

        Should.Throw<ArgumentNullException>(() => bundle.Wrap(null));
    }

    // ========== Helper exports ==========

    [Theory]
    [InlineData("SimpleName", "SimpleName")]
    [InlineData("Name.With.Dots", "Name_With_Dots")]
    [InlineData("Name-With-Dashes", "Name_With_Dashes")]
    [InlineData("Name With Spaces", "Name_With_Spaces")]
    [InlineData("123StartsWithDigit", "_123StartsWithDigit")]
    [InlineData("Name_WithUnderscore", "Name_WithUnderscore")]
    public void SanitizeEnvVarName_ProducesPosixSafeName(string input, string expected)
    {
        BashRuntimeBundle.SanitizeEnvVarName(input).ShouldBe(expected);
    }

    // ── B.6 (Phase-5, 2026-04-25 audit follow-up) — single-quote bash literal ─
    //
    // Pre-fix the escaper used double-quote wrapping with backslash escapes for
    // ", $, `, !, and \ — but did NOT escape newlines. A variable value
    // containing a literal `\n` terminated the `export VAR="..."` line, leaving
    // anything after parsed as the next bash command. Since variable values
    // come from operator config, output-variable capture, and other operator-
    // controlled paths (some of which can include adversarial content), this
    // was a confirmed shell-injection vector.
    //
    // The new contract: EscapeBashValue returns a FULLY-QUOTED single-quote
    // bash literal — caller does NOT wrap. Inside single quotes, EVERY
    // character is literal; the only escape needed is for ' itself, expressed
    // via the four-character POSIX idiom `'\''` (close-quote, escaped-quote,
    // reopen-quote). This eliminates the entire class of escape-bypass bugs
    // by removing all metacharacter interpretation.

    [Theory]
    [InlineData("plain text", "'plain text'")]
    [InlineData("", "''")]
    [InlineData(null, "''")]
    public void EscapeBashValue_BasicValue_ProducesFullySingleQuotedLiteral(string input, string expected)
    {
        BashRuntimeBundle.EscapeBashValue(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("has \"quote\"", "'has \"quote\"'")]
    [InlineData("has $dollar", "'has $dollar'")]
    [InlineData("has `backtick`", "'has `backtick`'")]
    [InlineData("has \\backslash", "'has \\backslash'")]
    [InlineData("has !bang", "'has !bang'")]
    [InlineData("has #hash", "'has #hash'")]
    [InlineData("has &amp", "'has &amp'")]
    public void EscapeBashValue_AllShellMetacharsLiteral_NoEscapingInsideSingleQuotes(string input, string expected)
    {
        // Inside single quotes nothing is interpreted. ALL of $, `, ", \, !, #, &
        // become literal — none need backslash escaping. This is the whole point
        // of switching from the previous double-quote+backslash scheme.
        BashRuntimeBundle.EscapeBashValue(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a'b'c", "'a'\\''b'\\''c'")]
    [InlineData("'", "''\\'''")]
    public void EscapeBashValue_EmbeddedSingleQuote_UsesPosixIdiom(string input, string expected)
    {
        // The ONLY character that needs special handling inside single quotes is
        // ' itself, which can't appear unescaped. POSIX idiom: close the current
        // single quote, write a backslash-escaped single quote, reopen single
        // quotes. Result: `'a'\''b'` reads as `a'b` after bash parsing.
        BashRuntimeBundle.EscapeBashValue(input).ShouldBe(expected);
    }

    [Fact]
    public void EscapeBashValue_NewlineEmbedded_DoesNotInjectShellCommand()
    {
        // The regression test for the actual exploit: a variable value that
        // contains an LF would, under the old double-quote scheme, terminate
        // the `export VAR="..."` line — anything after the LF being parsed as
        // the next bash command. Single-quote wrapping preserves the newline
        // INSIDE the quoted literal (a multi-line string), so the export
        // statement still ends at the closing quote.
        var malicious = "value\nrm -rf /tmp/squid-pwned";

        var escaped = BashRuntimeBundle.EscapeBashValue(malicious);

        // Single-quoted literal, newline preserved inside (no backslash
        // expansion in single quotes — the literal LF byte is part of the
        // value), closing quote AFTER the injected text.
        escaped.ShouldBe("'value\nrm -rf /tmp/squid-pwned'");

        // The whole literal is a single bash word. The exploit substring is
        // BETWEEN the single quotes — bash will not execute it.
        escaped.IndexOf("'", StringComparison.Ordinal).ShouldBe(0,
            customMessage: "literal MUST start with a single quote so bash treats the whole multi-line span as one word");
        escaped.LastIndexOf("'", StringComparison.Ordinal).ShouldBe(escaped.Length - 1,
            customMessage: "literal MUST end with a single quote — anything outside would be interpreted as a separate command");
    }

    [Fact]
    public void EscapeBashValue_CarriageReturn_StaysInsideQuotes()
    {
        // CR (Windows-line-ending) carries the same risk as LF in some shells.
        // Single-quote wrapping makes it inert.
        var value = "line1\r\nline2";

        BashRuntimeBundle.EscapeBashValue(value).ShouldBe("'line1\r\nline2'");
    }

    [Fact]
    public void Wrap_VariableValueWithNewline_NoInjectionInExportLine()
    {
        // End-to-end regression test through Wrap — the place an attacker would
        // actually reach this code path. A variable value with an embedded
        // newline used to break out of the `export` line; now it stays
        // contained inside the single-quoted literal.
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Bad.Var", Value = "harmless\nrm -rf /", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        // Single export line containing the multiline literal — the `rm -rf /`
        // is INSIDE the quotes, NOT a separate command.
        result.ShouldContain("export Bad_Var='harmless\nrm -rf /'");

        // Pre-fix the malicious payload would have appeared as a bare command
        // (no surrounding quotes on the same logical line). Pin that absence:
        // the literal `rm -rf /` MUST be embedded in a quoted span, not
        // standalone.
        var injectionLine = "\nrm -rf /\n";
        var allLines = result.Split('\n');
        allLines.Any(l => l.Trim() == "rm -rf /").ShouldBeFalse(
            customMessage: "the malicious payload escaped the quoted literal — shell injection regression!");
    }
}
