namespace Squid.Core.Services.Teams;

public static class TeamIdParser
{
    public static List<int> ParseCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return new();

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0).ToList();
    }
}
