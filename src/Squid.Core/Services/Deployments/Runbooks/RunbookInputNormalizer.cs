using System.Text.Json;
using Squid.Core.Services.Deployments.Runbooks.Exceptions;

namespace Squid.Core.Services.Deployments.Runbooks;

public static class RunbookInputNormalizer
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 4000;
    public const int TagMaxLength = 100;
    public const int TagsMaxCount = 50;
    public const int PageSizeMax = 100;

    private const string CopyNameSuffix = " (copy)";

    public static string NormalizeName(string value)
    {
        var name = value?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            throw Invalid("runbook.name_required", "Name", "Runbook name is required.");
        }

        if (name.Length > NameMaxLength)
        {
            throw Invalid(
                "runbook.name_too_long",
                "Name",
                $"Runbook name must be at most {NameMaxLength} characters.");
        }

        return name;
    }

    public static string NormalizeDescription(string value)
    {
        var description = value?.Trim();

        if (description?.Length > DescriptionMaxLength)
        {
            throw Invalid(
                "runbook.description_too_long",
                "Description",
                $"Runbook description must be at most {DescriptionMaxLength} characters.");
        }

        return description;
    }

    public static List<string> NormalizeTags(IEnumerable<string> values)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values ?? Enumerable.Empty<string>())
        {
            var tag = value?.Trim();
            if (string.IsNullOrEmpty(tag)) continue;

            if (tag.Length > TagMaxLength)
            {
                throw Invalid(
                    "runbook.tag_too_long",
                    "Tags",
                    $"Runbook tags must be at most {TagMaxLength} characters.");
            }

            if (seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        if (tags.Count > TagsMaxCount)
        {
            throw Invalid(
                "runbook.too_many_tags",
                "Tags",
                $"A Runbook can have at most {TagsMaxCount} tags.");
        }

        return tags;
    }

    public static string SerializeTags(IEnumerable<string> tags)
    {
        return JsonSerializer.Serialize(tags?.ToList() ?? new List<string>());
    }

    public static List<string> DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>())
                .Where(x => x != null)
                .ToList();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static string NormalizeCopyName(string sourceName, string requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return NormalizeName(requestedName);
        }

        var prefixLength = NameMaxLength - CopyNameSuffix.Length;
        var prefix = sourceName.Length > prefixLength
            ? sourceName[..prefixLength].TrimEnd()
            : sourceName;

        return NormalizeName($"{prefix}{CopyNameSuffix}");
    }

    public static (int PageIndex, int PageSize) NormalizePaging(int? pageIndex, int? pageSize)
    {
        var index = pageIndex.GetValueOrDefault();
        if (index < 1) index = 1;

        var size = pageSize.GetValueOrDefault();
        if (size < 1) size = 20;
        if (size > PageSizeMax) size = PageSizeMax;

        return (index, size);
    }

    private static RunbookValidationException Invalid(string code, string field, string message)
    {
        return new RunbookValidationException(code, field, message);
    }
}
