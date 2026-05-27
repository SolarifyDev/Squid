using Shouldly;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Variables;

/// <summary>
/// PR-4 — PowerShell preamble shape. Mirrors
/// <c>VariableBootstrapperTests</c> for the bash side; pins the env-var
/// syntax, name sanitisation, value escaping, and the UTF-8 stdout pin.
/// </summary>
public sealed class PowerShellVariableBootstrapperTests
{
    [Fact]
    public void GeneratePreamble_PinsUtf8StdoutBeforeVariables()
    {
        // Critical: chars from non-ASCII (Chinese / emoji) must round-trip.
        // The UTF-8 pin MUST appear BEFORE the first `$env:` line so encoding
        // is set before any output.
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["MyVar"] = "value" });

        var utf8Index = preamble.IndexOf("[Console]::OutputEncoding", StringComparison.Ordinal);
        var firstEnvIndex = preamble.IndexOf("$env:", StringComparison.Ordinal);

        utf8Index.ShouldBeGreaterThanOrEqualTo(0,
            customMessage: "UTF-8 OutputEncoding pin MUST appear in the preamble. " +
                           "Without it, Windows hosts mangle non-ASCII chars in captured logs.");
        utf8Index.ShouldBeLessThan(firstEnvIndex,
            customMessage: "UTF-8 pin MUST appear BEFORE first variable export so encoding is set before any output.");
    }

    [Fact]
    public void GeneratePreamble_EmitsEnvAssignment_SimpleNameAndValue()
    {
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["MyVar"] = "hello" });

        preamble.ShouldContain("$env:MyVar = 'hello'",
            customMessage: "PS env-var assignment syntax. Operator's script reads $env:MyVar.");
    }

    [Fact]
    public void GeneratePreamble_SanitisesDotsHyphensSlashes_SameAsBash()
    {
        // Same sanitiser as bash bootstrapper — operator's variable name
        // resolves under EITHER shell without renaming.
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string>
            {
                ["My.Dotted.Name"] = "v1",
                ["My-Hyphen-Name"] = "v2",
                ["My/Slash/Name"] = "v3"
            });

        preamble.ShouldContain("$env:My_Dotted_Name = 'v1'");
        preamble.ShouldContain("$env:My_Hyphen_Name = 'v2'");
        preamble.ShouldContain("$env:My_Slash_Name = 'v3'");
    }

    [Fact]
    public void GeneratePreamble_SkipsNamesStartingWithDigit()
    {
        // PowerShell variable names can't start with digit (after sanitisation).
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string>
            {
                ["1stVar"] = "skipped",
                ["ValidVar"] = "kept"
            });

        preamble.ShouldNotContain("1stVar", Case.Insensitive);
        preamble.ShouldContain("ValidVar");
    }

    [Fact]
    public void GeneratePreamble_EscapesSingleQuoteByDoubling()
    {
        // PS single-quote literal: '' escapes to a literal single quote.
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["Password"] = "it's a secret" });

        preamble.ShouldContain("$env:Password = 'it''s a secret'",
            customMessage: "Single quote in operator value MUST be escaped by doubling " +
                           "(PowerShell single-quote literal rule). Otherwise: parse error at deploy time.");
    }

    [Fact]
    public void GeneratePreamble_DoesNotEscapeDollarOrBacktick()
    {
        // Inside PS SINGLE-QUOTE literal, $ and ` are LITERAL. No escape needed.
        // Pinning this so a future "let's be safe and escape everything" refactor
        // doesn't break operator values containing `$foo` (e.g. cron syntax).
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["Cron"] = "$(date) `backtick`" });

        preamble.ShouldContain("$env:Cron = '$(date) `backtick`'",
            customMessage: "Single-quote PS literal does NOT interpret $ or backtick. " +
                           "Operator's literal '$(date)' MUST survive through the preamble.");
    }

    [Fact]
    public void GeneratePreamble_PreservesEmbeddedNewlines()
    {
        // Multi-line values (e.g. PEM keys) MUST survive verbatim.
        var multiline = "-----BEGIN-----\nline1\nline2\n-----END-----";
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(
            new Dictionary<string, string> { ["PrivateKey"] = multiline });

        preamble.ShouldContain(multiline,
            customMessage: "Embedded newlines in operator value MUST survive — same lossless behaviour as bash bootstrapper after B.6 audit.");
    }

    [Fact]
    public void GeneratePreamble_EmptyVariableSet_StillEmitsUtf8Pin()
    {
        // No variables → preamble is just the UTF-8 pin. Empty preamble
        // would let Windows host pick the OEM codepage default.
        var preamble = PowerShellVariableBootstrapper.GeneratePreamble(new Dictionary<string, string>());

        preamble.ShouldContain("[Console]::OutputEncoding");
        preamble.ShouldNotContain("$env:");
    }
}
