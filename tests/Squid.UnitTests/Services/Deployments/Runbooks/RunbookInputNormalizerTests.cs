using Squid.Core.Services.Deployments.Runbooks;
using Squid.Core.Services.Deployments.Runbooks.Exceptions;

namespace Squid.UnitTests.Services.Deployments.Runbooks;

public class RunbookInputNormalizerTests
{
    [Fact]
    public void NormalizeName_TrimsAndValidatesLength()
    {
        RunbookInputNormalizer.NormalizeName("  Restore database  ").ShouldBe("Restore database");

        Should.Throw<RunbookValidationException>(
            () => RunbookInputNormalizer.NormalizeName("   "));
        Should.Throw<RunbookValidationException>(
            () => RunbookInputNormalizer.NormalizeName(new string('a', 201)));
    }

    [Fact]
    public void NormalizeTags_TrimsDeduplicatesAndPreservesFirstCasing()
    {
        var tags = RunbookInputNormalizer.NormalizeTags(
            new[] { " recovery ", "RECOVERY", "", null, "ops" });

        tags.ShouldBe(new[] { "recovery", "ops" });
        RunbookInputNormalizer.SerializeTags(tags).ShouldBe("[\"recovery\",\"ops\"]");
    }

    [Fact]
    public void NormalizeCopyName_AddsSuffixAndRespectsMaximumLength()
    {
        RunbookInputNormalizer.NormalizeCopyName("Restore", null)
            .ShouldBe("Restore (copy)");

        var name = RunbookInputNormalizer.NormalizeCopyName(
            new string('a', RunbookInputNormalizer.NameMaxLength),
            null);

        name.Length.ShouldBe(RunbookInputNormalizer.NameMaxLength);
        name.ShouldEndWith(" (copy)");
    }

    [Fact]
    public void NormalizePaging_ClampsToBounds()
    {
        RunbookInputNormalizer.NormalizePaging(0, 0).ShouldBe((1, 20));
        RunbookInputNormalizer.NormalizePaging(-2, 500)
            .ShouldBe((1, RunbookInputNormalizer.PageSizeMax));
    }

    [Fact]
    public void DeserializeTags_InvalidJsonReturnsEmptyList()
    {
        RunbookInputNormalizer.DeserializeTags("not-json").ShouldBeEmpty();
        RunbookInputNormalizer.DeserializeTags(null).ShouldBeEmpty();
    }
}
