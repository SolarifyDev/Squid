using Shouldly;
using Squid.Tentacle.ScriptExecution.Masking;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution.Masking;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class SensitiveValueMaskerTests
{
    [Fact]
    public void Mask_NoPatterns_ReturnsInputUnchanged()
    {
        var masker = new SensitiveValueMasker(Array.Empty<string>());

        masker.Mask("nothing to hide").ShouldBe("nothing to hide");
    }

    [Fact]
    public void Mask_SinglePatternMatch_ReplacesWithMaskToken()
    {
        var masker = new SensitiveValueMasker(new[] { "supersecret-pw-123" });

        masker.Mask("login using supersecret-pw-123 for admin access")
            .ShouldBe("login using *** for admin access");
    }

    [Fact]
    public void Mask_MultipleOccurrences_AllReplaced()
    {
        var masker = new SensitiveValueMasker(new[] { "hunter2ab" });

        masker.Mask("password=hunter2ab again=hunter2ab")
            .ShouldBe("password=*** again=***");
    }

    [Fact]
    public void Mask_MultiplePatterns_AllReplaced()
    {
        var masker = new SensitiveValueMasker(new[] { "apiKey-ZXABC", "dbPass-QWER" });

        masker.Mask("connect with apiKey-ZXABC and dbPass-QWER")
            .ShouldBe("connect with *** and ***");
    }

    [Fact]
    public void Mask_OverlappingPatterns_PrefersLongestAtEachPosition()
    {
        var masker = new SensitiveValueMasker(new[] { "secret-token", "secret-token-123" });

        masker.Mask("use secret-token-123 now")
            .ShouldBe("use *** now");
    }

    [Fact]
    public void Mask_PatternShorterThanMinLength_Ignored()
    {
        var masker = new SensitiveValueMasker(new[] { "a", "ab", "abc" });   // all < MinPatternLength=4

        masker.PatternCount.ShouldBe(0, "sub-minimum patterns would mask half the log stream — must be discarded");
        masker.Mask("abc def").ShouldBe("abc def");
    }

    [Fact]
    public void Mask_PatternAtStart_And_AtEnd_Replaced()
    {
        var masker = new SensitiveValueMasker(new[] { "PASSWORD123" });

        masker.Mask("PASSWORD123 in the middle PASSWORD123")
            .ShouldBe("*** in the middle ***");
    }

    [Fact]
    public void Mask_EmptyString_ReturnsEmpty()
    {
        var masker = new SensitiveValueMasker(new[] { "secret123" });

        masker.Mask("").ShouldBe("");
    }

    [Fact]
    public void Mask_CustomToken_Honoured()
    {
        var masker = new SensitiveValueMasker(new[] { "secret123" }, maskToken: "[REDACTED]");

        masker.Mask("echo secret123").ShouldBe("echo [REDACTED]");
    }

    [Fact]
    public void Mask_LargeHaystackManyPatterns_PerformanceIsLinearish()
    {
        // Sanity: 500 patterns × 50 KB log line should complete well under a second.
        var patterns = Enumerable.Range(0, 500).Select(i => $"secret-pattern-{i:D6}").ToList();
        var masker = new SensitiveValueMasker(patterns);

        var haystack = string.Concat(Enumerable.Range(0, 1000).Select(i => $"normal-log-line-number-{i} "));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = masker.Mask(haystack);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(500, "O(n + m) scan must not regress to O(n * m)");
        result.ShouldBe(haystack, "no patterns are actually present — output must equal input");
    }

    [Fact]
    public void Mask_DuplicatePatterns_TreatedAsOne()
    {
        var masker = new SensitiveValueMasker(new[] { "dupe-pat", "dupe-pat", "dupe-pat" });

        masker.PatternCount.ShouldBe(1);
        masker.Mask("x dupe-pat y").ShouldBe("x *** y");
    }
}
