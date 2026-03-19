using System;
using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class SensitiveValueMaskerTests
{
    [Fact]
    public void Mask_ReplacesKnownSensitiveValues()
    {
        var masker = new SensitiveValueMasker(new[] { "my-secret-token" });

        var result = masker.Mask("Connecting with token my-secret-token to server");

        result.ShouldBe($"Connecting with token {SensitiveValueMasker.MaskToken} to server");
    }

    [Fact]
    public void Mask_ReplacesMultipleDistinctValues()
    {
        var masker = new SensitiveValueMasker(new[] { "password123", "api-key-xyz" });

        var result = masker.Mask("auth=password123 key=api-key-xyz");

        result.ShouldBe($"auth={SensitiveValueMasker.MaskToken} key={SensitiveValueMasker.MaskToken}");
    }

    [Fact]
    public void Mask_ReplacesMultipleOccurrencesOfSameValue()
    {
        var masker = new SensitiveValueMasker(new[] { "secret" });

        var result = masker.Mask("first secret then secret again");

        result.ShouldBe($"first {SensitiveValueMasker.MaskToken} then {SensitiveValueMasker.MaskToken} again");
    }

    [Fact]
    public void Mask_LongerValuesReplacedFirst_PreventsPartialMatch()
    {
        var masker = new SensitiveValueMasker(new[] { "pass", "password123" });

        var result = masker.Mask("using password123 here");

        result.ShouldBe($"using {SensitiveValueMasker.MaskToken} here");
    }

    [Fact]
    public void Mask_IgnoresValuesShort()
    {
        var masker = new SensitiveValueMasker(new[] { "ab", "", " ", null });

        masker.ValueCount.ShouldBe(0);
        masker.Mask("ab test").ShouldBe("ab test");
    }

    [Fact]
    public void Mask_ReturnsOriginalWhenNoSensitiveValues()
    {
        var masker = new SensitiveValueMasker(Array.Empty<string>());

        masker.Mask("hello world").ShouldBe("hello world");
    }

    [Fact]
    public void Mask_ReturnsOriginalWhenTextIsNullOrEmpty()
    {
        var masker = new SensitiveValueMasker(new[] { "secret" });

        masker.Mask(null).ShouldBeNull();
        masker.Mask("").ShouldBe("");
    }

    [Fact]
    public void Mask_ReturnsOriginalWhenNoMatch()
    {
        var masker = new SensitiveValueMasker(new[] { "secret" });

        masker.Mask("nothing sensitive here").ShouldBe("nothing sensitive here");
    }

    [Fact]
    public void Mask_DeduplicatesValues()
    {
        var masker = new SensitiveValueMasker(new[] { "token", "token", "token" });

        masker.ValueCount.ShouldBe(1);
        masker.Mask("using token").ShouldBe($"using {SensitiveValueMasker.MaskToken}");
    }

    [Fact]
    public void Mask_HandlesSpecialRegexCharactersInValues()
    {
        var masker = new SensitiveValueMasker(new[] { "p@ss.w0rd+1" });

        masker.Mask("login with p@ss.w0rd+1 ok").ShouldBe($"login with {SensitiveValueMasker.MaskToken} ok");
    }

    [Fact]
    public void Mask_IsThreadSafe()
    {
        var masker = new SensitiveValueMasker(new[] { "secret-value" });

        Parallel.For(0, 100, _ =>
        {
            var result = masker.Mask("token=secret-value&key=secret-value");
            result.ShouldBe($"token={SensitiveValueMasker.MaskToken}&key={SensitiveValueMasker.MaskToken}");
        });
    }
}
