using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSessionExpiryTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(168);

    [Fact]
    public void Deadline_IsCreatedPlusMaxAge()
    {
        OctopusImportSessionExpiry.Deadline(Created, MaxAge).ShouldBe(Created.AddDays(7));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void Clamp_NeverPastTheDeadline(int candidateHoursFromDeadline, int expectedHoursFromDeadline)
    {
        var deadline = Created.Add(MaxAge);

        OctopusImportSessionExpiry.Clamp(deadline.AddHours(candidateHoursFromDeadline), Created, MaxAge).ShouldBe(deadline.AddHours(expectedHoursFromDeadline));
    }

    // At the deadline itself the session is over age: the sweep (CreatedDate <= now - maxAge) and the guard agree on the boundary.
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IsOverAge_InclusiveAtTheDeadline(int secondsFromDeadline, bool expected)
    {
        OctopusImportSessionExpiry.IsOverAge(Created, MaxAge, Created.Add(MaxAge).AddSeconds(secondsFromDeadline)).ShouldBe(expected);
    }

    [Fact]
    public void MaxAgeReachedResult_HasBlockerCodeAndHoursAndNoSucceeded()
    {
        var now = DateTimeOffset.UtcNow;

        var result = OctopusImportSessionExpiry.MaxAgeReachedResult(TimeSpan.FromHours(36), now);

        result.Succeeded.ShouldBeFalse();
        result.CompletedAt.ShouldBe(now);
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        diagnostic.Code.ShouldBe("octopus.session.max_age_reached");
        diagnostic.Message.ShouldContain("36 hours");
    }

    [Fact]
    public void MaxAgeReachedCode_IsAWireContract()
    {
        OctopusImportSessionExpiry.MaxAgeReachedCode.ShouldBe("octopus.session.max_age_reached");
    }
}
