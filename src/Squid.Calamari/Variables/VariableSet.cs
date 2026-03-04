using System.Collections;
using System.Globalization;

namespace Squid.Calamari.Variables;

public sealed class VariableSet : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, VariableEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public IEnumerable<VariableEntry> Entries => _entries.Values;

    public string? Get(string name, string? defaultValue = null)
    {
        if (string.IsNullOrEmpty(name))
            return defaultValue;

        return _entries.TryGetValue(name, out var entry)
            ? entry.Value
            : defaultValue;
    }

    public bool Contains(string name)
        => !string.IsNullOrEmpty(name) && _entries.ContainsKey(name);

    public void Set(string name, string? value, bool isSensitive = false, string? source = null)
    {
        if (string.IsNullOrEmpty(name))
            return;

        _entries[name] = new VariableEntry(name, value ?? string.Empty, isSensitive, source);
    }

    public void Merge(IEnumerable<VariableEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name))
                continue;

            Set(entry.Name, entry.Value, entry.IsSensitive, entry.Source);
        }
    }

    public bool GetFlag(string name, bool defaultValue = false)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (bool.TryParse(value, out var boolValue))
            return boolValue;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return intValue != 0;

        return defaultValue;
    }

    public int? GetInt32(string name)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        => _entries.Values
            .Select(e => new KeyValuePair<string, string>(e.Name, e.Value))
            .GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
