namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.Helm;

internal static class HelmIndexParser
{
    internal static string ParseChartUrl(string indexYaml, string packageId, string version, string baseUri)
    {
        // Helm index.yaml fields (version, urls) can appear in any order within an entry.
        // We collect each entry as a block, then independently extract version + url.
        var lines = indexYaml.Split('\n');
        var entryBlocks = ExtractChartEntryBlocks(lines, packageId);

        foreach (var block in entryBlocks)
        {
            var url = MatchEntryVersionAndUrl(block, version, baseUri);

            if (url != null)
                return url;
        }

        return null;
    }

    private static List<List<string>> ExtractChartEntryBlocks(string[] lines, string packageId)
    {
        var blocks = new List<List<string>>();
        var inTargetChart = false;
        var entryIndent = -1;
        List<string> currentBlock = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();

            if (!inTargetChart)
            {
                var stripped = trimmed.TrimStart();

                if (stripped.Equals($"{packageId}:", StringComparison.OrdinalIgnoreCase)
                    || stripped.Equals($"\"{packageId}\":", StringComparison.OrdinalIgnoreCase))
                    inTargetChart = true;

                continue;
            }

            if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith(" ") && !trimmed.StartsWith("\t"))
                break;

            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var indent = trimmed.Length - trimmed.TrimStart().Length;
            var content = trimmed.TrimStart();

            if (content.StartsWith("- ") && entryIndent < 0)
                entryIndent = indent;

            if (content.StartsWith("- ") && indent == entryIndent)
            {
                currentBlock = new List<string>();
                blocks.Add(currentBlock);
            }

            currentBlock?.Add(content);
        }

        return blocks;
    }

    private static string MatchEntryVersionAndUrl(List<string> entryLines, string targetVersion, string baseUri)
    {
        string entryVersion = null;
        string entryUrl = null;
        var inUrls = false;

        foreach (var line in entryLines)
        {
            if (line.StartsWith("- version:", StringComparison.OrdinalIgnoreCase))
            {
                entryVersion = line["- version:".Length..].Trim().Trim('"', '\'');
                inUrls = false;
                continue;
            }

            if (line.StartsWith("version:", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("versions:", StringComparison.OrdinalIgnoreCase))
            {
                entryVersion = line["version:".Length..].Trim().Trim('"', '\'');
                inUrls = false;
                continue;
            }

            if (line.StartsWith("urls:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("- urls:", StringComparison.OrdinalIgnoreCase))
            {
                inUrls = true;
                continue;
            }

            if (inUrls && line.StartsWith("- ") && entryUrl == null)
            {
                entryUrl = line[2..].Trim().Trim('"', '\'');
                inUrls = false;
                continue;
            }

            if (inUrls && !line.StartsWith("- "))
                inUrls = false;
        }

        if (entryVersion == null || !string.Equals(entryVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            return null;

        if (entryUrl == null) return null;

        if (entryUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || entryUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return entryUrl;

        return $"{baseUri.TrimEnd('/')}/{entryUrl}";
    }
}
