using System.Linq;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Core.Services.Packages.Versioning.Tokens;
using Squid.Message.Models.PackageVersioning;

namespace Squid.UnitTests.Services.Packages.Versioning;

public class CounterAndDateTokenTests
{
    private static readonly DateTimeOffset Evening = new(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);

    private readonly FakeCounterStore _store = new();
    private readonly FixedTimeProvider _clock = new(Evening);

    private CounterToken Counter => new(_store, _clock);

    [Fact]
    public async Task Counter_TakesTheNumberOfTheScopeBuiltFromTheTextBeforeIt()
    {
        var context = VersioningTestContexts.Context() with { PrecedingText = "1.0." };

        (await Counter.ResolveAsync(context, null, CancellationToken.None)).ShouldBe("1");
        (await Counter.ResolveAsync(context, null, CancellationToken.None)).ShouldBe("2");

        _store.Calls.ShouldBe([(7, "Acme.Web", "1.0."), (7, "Acme.Web", "1.0.")]);
    }

    [Fact]
    public async Task Counter_Daily_AddsTheUtcDayToTheScope_AndStartsAgainTheNextDay()
    {
        var context = VersioningTestContexts.Context() with { PrecedingText = "1.0." };

        (await Counter.ResolveAsync(context, "daily", CancellationToken.None)).ShouldBe("1");
        (await Counter.ResolveAsync(context, "DAILY", CancellationToken.None)).ShouldBe("2");

        _clock.UtcNow = Evening.AddHours(5);

        (await Counter.ResolveAsync(context, "daily", CancellationToken.None)).ShouldBe("1");

        _store.Calls.Select(c => c.Scope).ShouldBe(["1.0.|daily:2026-11-25", "1.0.|daily:2026-11-25", "1.0.|daily:2026-11-26"]);
    }

    [Fact]
    public async Task Counter_DryRun_GivesOneAndNeverTouchesTheStore()
    {
        var context = VersioningTestContexts.Context() with { DryRun = true, PrecedingText = "1.0." };

        (await Counter.ResolveAsync(context, null, CancellationToken.None)).ShouldBe("1");
        (await Counter.ResolveAsync(context, "daily", CancellationToken.None)).ShouldBe("1");

        _store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Counter_UnknownArgument_NamesTheOnlyOne_EvenInADryRun()
    {
        var context = VersioningTestContexts.Context() with { DryRun = true };

        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => Counter.ResolveAsync(context, "weekly", CancellationToken.None));

        ex.Message.ShouldBe("Version token '{counter}' cannot take the argument 'weekly': the only argument is 'daily'");
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
    public async Task Date_DefaultsToYearMonthDayInUtc_NotLocalTime()
    {
        var value = await new DateToken(_clock).ResolveAsync(VersioningTestContexts.Context(), null, CancellationToken.None);

        value.ShouldBe("2026.11.25");
    }

    [Fact]
    public async Task Date_UsesTheGivenFormat()
    {
        var value = await new DateToken(_clock).ResolveAsync(VersioningTestContexts.Context(), "yyyyMMdd-HHmm", CancellationToken.None);

        value.ShouldBe("20261125-2000");
    }

    [Fact]
    public async Task Date_InvalidFormat_SaysWhichOne()
    {
        var ex = await Should.ThrowAsync<InvalidVersionTokenArgumentException>(() => new DateToken(_clock).ResolveAsync(VersioningTestContexts.Context(), "%", CancellationToken.None));

        ex.Message.ShouldStartWith("Version token '{date}' cannot take the argument '%': it is not a valid date format");
    }
}
