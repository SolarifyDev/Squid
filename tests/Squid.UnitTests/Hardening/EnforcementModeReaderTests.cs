using System;
using Squid.Message.Hardening;

namespace Squid.UnitTests.Hardening;

/// <summary>
/// Pin the parsing vocabulary of <see cref="EnforcementModeReader"/>. This helper
/// is used by every hardening check across the codebase — drift in the recognised
/// spellings would silently degrade what an operator's env var means
/// (e.g. <c>SQUID_X=enforce</c> stops being Strict and silently falls to Warn).
///
/// <para>Operators don't read code. The vocabulary documented in the XML doc
/// comment of <see cref="EnforcementModeReader"/> is the contract; these tests
/// are the spec.</para>
/// </summary>
public sealed class EnforcementModeReaderTests
{
    private const string TestEnvVar = "SQUID_ENFORCEMENT_MODE_READER_TEST";

    public EnforcementModeReaderTests()
    {
        // Each test owns the env var. Clear before so prior runs / parallel tests
        // can't contaminate. We don't run in parallel within a class, but defence
        // in depth is cheap.
        Environment.SetEnvironmentVariable(TestEnvVar, null);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("Off")]
    [InlineData("disabled")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData(" off ")]    // whitespace tolerance
    public void Read_OffSpellings_ReturnOff(string raw)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);

        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Off,
            customMessage: $"'{raw}' must parse as Off — operator-facing vocabulary");
    }

    [Theory]
    [InlineData("warn")]
    [InlineData("WARN")]
    [InlineData("Warn")]
    [InlineData("warning")]
    public void Read_WarnSpellings_ReturnWarn(string raw)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);

        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Warn);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("STRICT")]
    [InlineData("enforce")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    public void Read_StrictSpellings_ReturnStrict(string raw)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);

        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Strict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Read_UnsetOrBlank_ReturnsDefault(string raw)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);

        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Warn,
            customMessage: "default-when-unset is Warn — preserves backward compat (this is THE rule)");

        EnforcementModeReader.Read(TestEnvVar, EnforcementMode.Strict).ShouldBe(EnforcementMode.Strict,
            customMessage: "explicit defaultMode argument must override the Warn baseline");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("typo-strrict")]
    [InlineData("garbage")]
    public void Read_UnrecognisedValue_FallsBackToDefault_NeverThrows(string raw)
    {
        // Unrecognised value MUST NOT crash the process — operators typo env vars,
        // and a hard fail-fast there would block startup over a typo. Fall back
        // to the documented default and let the warning log prompt the operator.
        Environment.SetEnvironmentVariable(TestEnvVar, raw);

        Should.NotThrow(() => EnforcementModeReader.Read(TestEnvVar));
        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Warn);
    }
}
