using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The hard cap on an import session's life: however much activity slides its expiry, a session is never kept past its creation plus
/// the configured maximum age. The guard, the admission and the sweep all use these, so they agree on the boundary (inclusive).
/// </summary>
public static class OctopusImportSessionExpiry
{
    public const string MaxAgeReachedCode = "octopus.session.max_age_reached";

    public static DateTimeOffset Deadline(DateTimeOffset createdAt, TimeSpan maxAge) => createdAt.Add(maxAge);

    public static DateTimeOffset Clamp(DateTimeOffset candidate, DateTimeOffset createdAt, TimeSpan maxAge) => candidate <= Deadline(createdAt, maxAge) ? candidate : Deadline(createdAt, maxAge);

    public static bool IsOverAge(DateTimeOffset createdAt, TimeSpan maxAge, DateTimeOffset now) => now >= Deadline(createdAt, maxAge);

    public static string MaxAgeReachedMessage(TimeSpan maxAge) => $"This import session reached its maximum age of {maxAge.TotalHours:0.#} hours and was closed. Nothing was imported by it; start a new import session (an import never overwrites what is already in Squid).";

    public static OctopusImportSessionResultDto MaxAgeReachedResult(TimeSpan maxAge, DateTimeOffset now) => new() { Succeeded = false, CompletedAt = now, Diagnostics = [MaxAgeReachedDiagnostic(maxAge)] };

    private static OctopusImportDiagnosticDto MaxAgeReachedDiagnostic(TimeSpan maxAge) => new() { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = MaxAgeReachedCode, Message = MaxAgeReachedMessage(maxAge) };
}
