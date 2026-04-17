namespace Squid.Tentacle.ScriptExecution.Masking;

/// <summary>
/// Replaces every occurrence of any sensitive value in a text with the mask
/// token (default <c>***</c>). Uses <see cref="AhoCorasickAutomaton"/> so the
/// cost of scanning a log line is O(line length) regardless of how many
/// sensitive values are registered.
///
/// Empty patterns and patterns shorter than <see cref="MinPatternLength"/> are
/// skipped. Without the minimum length guard, a sensitive value of "a" would
/// mask every "a" in every log line — a real production hazard when secret
/// material includes single letters or digits.
/// </summary>
public sealed class SensitiveValueMasker
{
    public const string DefaultMaskToken = "***";
    public const int MinPatternLength = 4;

    private readonly AhoCorasickAutomaton _automaton;
    private readonly string _maskToken;

    public SensitiveValueMasker(IEnumerable<string> sensitiveValues, string maskToken = DefaultMaskToken)
    {
        if (sensitiveValues == null) throw new ArgumentNullException(nameof(sensitiveValues));

        var filtered = sensitiveValues
            .Where(v => !string.IsNullOrEmpty(v) && v.Length >= MinPatternLength)
            .Distinct()
            .ToList();

        _automaton = new AhoCorasickAutomaton(filtered);
        _maskToken = maskToken ?? DefaultMaskToken;
    }

    public int PatternCount => _automaton.PatternCount;

    public string Mask(string input)
    {
        if (string.IsNullOrEmpty(input) || _automaton.PatternCount == 0) return input;

        var matches = _automaton.FindAll(input);
        if (matches.Count == 0) return input;

        var sb = new System.Text.StringBuilder(input.Length);
        var cursor = 0;

        foreach (var m in matches)
        {
            if (m.Start > cursor)
                sb.Append(input, cursor, m.Start - cursor);
            sb.Append(_maskToken);
            cursor = m.Start + m.Length;
        }

        if (cursor < input.Length)
            sb.Append(input, cursor, input.Length - cursor);

        return sb.ToString();
    }
}
