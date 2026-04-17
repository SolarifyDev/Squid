namespace Squid.Tentacle.ScriptExecution.Masking;

/// <summary>
/// Aho-Corasick multi-pattern string matcher — O(n + m + z) where n is the
/// haystack length, m is total pattern length, and z is the number of matches.
/// Naive regex-per-pattern is O(n * sum(m_i)) which collapses on log streams
/// with dozens of secrets × multi-megabyte scripts.
///
/// Build the automaton once with all sensitive values, then call
/// <see cref="FindAll"/> on each log line. The returned match list is
/// non-overlapping (longest-first resolution) so masking replaces every
/// longest secret occurrence exactly once.
/// </summary>
public sealed class AhoCorasickAutomaton
{
    private sealed class Node
    {
        public Dictionary<char, Node> Children { get; } = new();
        public Node Fail { get; set; }
        public int? PatternIndex { get; set; }
        public int PatternLength { get; set; }
    }

    private readonly Node _root = new();
    private readonly int _patternCount;

    public AhoCorasickAutomaton(IReadOnlyList<string> patterns)
    {
        if (patterns == null) throw new ArgumentNullException(nameof(patterns));

        var filtered = patterns.Where(p => !string.IsNullOrEmpty(p)).ToList();
        _patternCount = filtered.Count;

        BuildTrie(filtered);
        BuildFailureLinks();
    }

    public int PatternCount => _patternCount;

    public IReadOnlyList<Match> FindAll(string haystack)
    {
        if (string.IsNullOrEmpty(haystack) || _patternCount == 0)
            return Array.Empty<Match>();

        var matches = new List<Match>();
        var node = _root;

        for (var i = 0; i < haystack.Length; i++)
        {
            var ch = haystack[i];

            while (node != _root && !node.Children.ContainsKey(ch))
                node = node.Fail;

            if (node.Children.TryGetValue(ch, out var next))
                node = next;

            var walk = node;
            while (walk != _root)
            {
                if (walk.PatternIndex.HasValue)
                    matches.Add(new Match(i - walk.PatternLength + 1, walk.PatternLength));
                walk = walk.Fail;
            }
        }

        return CollapseToLongestNonOverlapping(matches);
    }

    private void BuildTrie(IReadOnlyList<string> patterns)
    {
        for (var p = 0; p < patterns.Count; p++)
        {
            var node = _root;
            var pattern = patterns[p];

            foreach (var ch in pattern)
            {
                if (!node.Children.TryGetValue(ch, out var child))
                {
                    child = new Node();
                    node.Children[ch] = child;
                }
                node = child;
            }

            // Preserve the longest pattern if the same string is registered twice.
            if (!node.PatternIndex.HasValue || node.PatternLength < pattern.Length)
            {
                node.PatternIndex = p;
                node.PatternLength = pattern.Length;
            }
        }
    }

    private void BuildFailureLinks()
    {
        var queue = new Queue<Node>();

        foreach (var child in _root.Children.Values)
        {
            child.Fail = _root;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            foreach (var (ch, child) in node.Children)
            {
                queue.Enqueue(child);

                var fail = node.Fail;
                while (fail != null && !fail.Children.ContainsKey(ch))
                    fail = fail.Fail;

                child.Fail = fail?.Children.TryGetValue(ch, out var f) == true ? f : _root;

                if (!child.PatternIndex.HasValue && child.Fail.PatternIndex.HasValue)
                {
                    child.PatternIndex = child.Fail.PatternIndex;
                    child.PatternLength = child.Fail.PatternLength;
                }
            }
        }
    }

    /// <summary>
    /// Keep only the longest match starting at each position, then drop any match
    /// whose start falls inside an earlier kept match's range. The result is
    /// ordered by start index with no overlaps — the mask replacer can apply them
    /// back-to-front in a single pass.
    /// </summary>
    private static IReadOnlyList<Match> CollapseToLongestNonOverlapping(List<Match> matches)
    {
        if (matches.Count == 0) return matches;

        var bestPerStart = new Dictionary<int, int>();
        foreach (var m in matches)
        {
            if (!bestPerStart.TryGetValue(m.Start, out var existing) || m.Length > existing)
                bestPerStart[m.Start] = m.Length;
        }

        var ordered = bestPerStart
            .Select(kv => new Match(kv.Key, kv.Value))
            .OrderBy(m => m.Start)
            .ToList();

        var result = new List<Match>(ordered.Count);
        var lastEnd = -1;
        foreach (var m in ordered)
        {
            if (m.Start < lastEnd) continue;
            result.Add(m);
            lastEnd = m.Start + m.Length;
        }
        return result;
    }

    public readonly record struct Match(int Start, int Length);
}
