using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Content fingerprints stored in the source map so a re-import can tell "unchanged" from "changed".
/// Each one covers exactly what an in-place update would write, after one canonicalisation: times in UTC
/// ISO-8601 truncated to milliseconds, null as "", strings trimmed, unordered collections sorted ordinally.
/// The canonical fields are JSON-encoded (unambiguous) and hashed as SHA-256 lowercase hex over
/// <see cref="VersionPrefix"/> + payload, so a future canonical change bumps the prefix and never collides.
/// </summary>
public static class OctopusImportFingerprint
{
    public const string VersionPrefix = "v1|";

    public const int DefaultMaxLogLinesPerTask = 20000;

    /// <summary>A release update writes only its notes; version and selected packages are never re-applied.</summary>
    public static string ForRelease(OctopusReleaseDto release)
        => release == null ? null : Digest(Encode([Canon(release.ReleaseNotes)]));

    /// <summary>
    /// The task scalars an update writes. Duration is not hashed on its own: it is derived from the start
    /// and completed times (both hashed), and the REST task resource does not carry <c>DurationSeconds</c>.
    /// </summary>
    public static string ForTask(OctopusServerTaskDto task)
        => task == null ? null : Digest(Encode([Canon(task.State), Canon(task.QueueTime), Canon(task.StartTime), Canon(task.CompletedTime), Canon(task.ErrorMessage), Canon(task.HasWarningsOrErrors)]));

    /// <summary>The activity tree, the capped log lines, interruptions, events and artifacts; null when there are no details.</summary>
    public static string ForTaskDetails(OctopusTaskHistoryDetails details, int maxLogLines = DefaultMaxLogLinesPerTask)
    {
        if (!HasDetails(details)) return null;

        var records = new List<object>();

        records.AddRange(ActivityRecords(details.RootActivities, maxLogLines));
        records.Add(Sorted((details.Interruptions ?? []).Select(InterruptionRecord)));
        records.Add(Sorted((details.Events ?? []).Select(EventRecord)));
        records.Add(Sorted((details.Artifacts ?? []).Select(ArtifactRecord)));

        return Digest(Encode(records));
    }

    /// <summary>Octopus's own change marker for a configuration document: DataVersion, else LastModified, else null.</summary>
    public static string ForConfig(OctopusDocumentDto document)
    {
        if (!string.IsNullOrWhiteSpace(document?.DataVersion)) return Digest(Encode(["DataVersion", Canon(document.DataVersion)]));

        return document?.LastModified == null ? null : Digest(Encode(["LastModified", Canon(document.LastModified)]));
    }

    internal static string Digest(string canonicalPayload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(VersionPrefix + canonicalPayload))).ToLowerInvariant();

    private static bool HasDetails(OctopusTaskHistoryDetails details)
        => details != null && (details.RootActivities.Count > 0 || details.Interruptions is { Count: > 0 } || details.Events is { Count: > 0 } || details.Artifacts is { Count: > 0 });

    // Pre-order: a node, then its own log lines, then its children — the order the lines are written in.
    // Log lines past the cap are not written, so they are not hashed; only their count is.
    private static IEnumerable<object> ActivityRecords(IReadOnlyList<OctopusActivityLogDto> roots, int maxLogLines)
    {
        var records = new List<object>();
        var lines = 0;
        var truncated = 0;

        void Visit(OctopusActivityLogDto node, int depth)
        {
            records.Add(ActivityRecord(node, depth));

            foreach (var line in node.LogElements ?? [])
            {
                if (lines++ < maxLogLines) records.Add(LogRecord(line));
                else truncated++;
            }

            foreach (var child in node.Children ?? [])
                Visit(child, depth + 1);
        }

        foreach (var root in roots)
            Visit(root, 0);

        records.Add(new object[] { "Truncated", truncated });

        return records;
    }

    private static object[] ActivityRecord(OctopusActivityLogDto node, int depth)
        => ["A", depth, Canon(node.Id), Canon(node.Name), Canon(node.Status), Canon(node.Started), Canon(node.Ended)];

    private static object[] LogRecord(OctopusLogElementDto line)
        => ["L", Canon(line.Category), Canon(line.OccurredAt), Canon(line.MessageText), Canon(line.Detail)];

    private static string InterruptionRecord(OctopusInterruptionDto interruption)
        => Encode([Canon(interruption.Id), Canon(interruption.Title), Canon(interruption.Type), Canon(interruption.IsPending), Canon(interruption.Created), Canon(interruption.TaskId), SortedValues(interruption.ResponsibleTeamIds), Canon(interruption.ResponsibleUserId), FormValues(interruption.Form)]);

    private static string EventRecord(OctopusEventDto @event)
        => Encode([Canon(@event.Id), Canon(@event.Category), Canon(@event.Occurred), Canon(@event.Message), Canon(@event.Username)]);

    private static string ArtifactRecord(OctopusArtifactDto artifact)
        => Encode([Canon(artifact.Id), Canon(artifact.Filename), Canon(artifact.Source), Canon(artifact.Created)]);

    private static List<string> FormValues(OctopusInterruptionFormDto form)
        => Sorted((form?.Values ?? []).Select(pair => Encode([Canon(pair.Key), Canon(pair.Value)])));

    private static List<string> SortedValues(IEnumerable<string> values)
        => Sorted((values ?? []).Select(Canon));

    private static List<string> Sorted(IEnumerable<string> values)
        => values.OrderBy(value => value, StringComparer.Ordinal).ToList();

    private static string Encode(IEnumerable<object> fields) => JsonSerializer.Serialize(fields);

    private static string Canon(string value) => value?.Trim() ?? string.Empty;

    private static string Canon(bool value) => value ? "true" : "false";

    private static string Canon(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
}
