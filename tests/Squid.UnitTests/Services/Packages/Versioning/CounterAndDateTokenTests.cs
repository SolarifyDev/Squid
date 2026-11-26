using System.Linq;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Core.Services.Packages.Versioning.Tokens;
using Squid.Message.Models.PackageVersioning;

namespace Squid.UnitTests.Services.Packages.Versioning;

public class CounterAndDateTokenTests
{
    private static readonly DateTimeOffset Evening = new(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);

    private readonly FakeCounterStore _store = new();

    private CounterToken Counter => new(_store);

    private static VersionTokenContext At(DateTimeOffset now) => VersioningTestContexts.Context() with { Now = now };

    [Fact]
    public async Task Counter_TakesTheNumberOfTheScopeBuiltFromTheTextBeforeIt()
    {
        var context = VersioningTestContexts.Context() with { PrecedingText = "1.0." };

        (await Counter.ResolveAsync(context, null, CancellationToken.None)).ShouldBe("1");
        (await Counter.ResolveAsync(context, null, CancellationToken.None)).ShouldBe("2");

        _store.Calls.ShouldBe([(7, "Acme.Web", "1.0."), (7, "Acme.Web", "1.0.")]);
    }

    [Fact]
    public async Task Counter_KeysOnTheTrimmedPackageId()
    {
        var context = VersioningTestContexts.Context() with { PackageId = "  Acme.Web \t", PrecedingText = "1.0." };

        await Counter.ResolveAsync(context, null, CancellationToken.None);

        _store.Calls.Single().PackageId.ShouldBe("Acme.Web");
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("weekly")]
    public async Task Counter_TakesNoArgument_EvenInADryRun(string argument)
    {
        var context = VersioningTestContexts.Context() with { DryRun = true };

        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => Counter.ResolveAsync(context, argument, CancellationToken.None));

        ex.Message.ShouldBe($"Version token '{{counter}}' cannot take the argument '{argument}': this token takes no argument; to count per day put a date before it, for example {{date:yyyy.MM.dd}}.{{counter}}");
        _store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Counter_DryRun_GivesOneAndNeverTouchesTheStore()
    {
        var context = VersioningTestContexts.Context() with { DryRun = true, PrecedingText = "1.0." };

        (await Counter.ResolveAsync(context, null, CancellationToken.None)).ShouldBe("1");

        _store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Counter_WithoutPackageId_SaysSoInsteadOfCountingForNobody()
    {
        var context = VersioningTestContexts.Context() with { PackageId = " " };

        var ex = await Should.ThrowAsync<VersionTokenValueMissingException>(() => Counter.ResolveAsync(context, null, CancellationToken.None));

        ex.Message.ShouldBe("Version token '{counter}' has no value: the package id is not known, so there is no counter to take a number from");
        _store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Date_DefaultsToYearMonthDayInUtc()
    {
        var value = await new DateToken().ResolveAsync(At(Evening), null, CancellationToken.None);

        value.ShouldBe("2026.11.25");
    }

    [Fact]
    public async Task Date_IsUtcEvenWhenTheClockReadingCarriesAnotherOffset()
    {
        // 2026-11-26 05:00 at +9 is still the 25th in UTC.
        var value = await new DateToken().ResolveAsync(At(new DateTimeOffset(2026, 11, 26, 5, 0, 0, TimeSpan.FromHours(9))), null, CancellationToken.None);

        value.ShouldBe("2026.11.25");
    }

    [Fact]
    public async Task Date_UsesTheGivenFormat()
    {
        var value = await new DateToken().ResolveAsync(At(Evening), "yyyyMMdd-HHmm", CancellationToken.None);

        value.ShouldBe("20261125-2000");
    }

    [Fact]
    public async Task Date_InvalidFormat_SaysWhichOne()
    {
        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => new DateToken().ResolveAsync(At(Evening), "%", CancellationToken.None));

        ex.Message.ShouldStartWith("Version token '{date}' cannot take the argument '%': it is not a valid date format");
    }
}
