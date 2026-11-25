using Squid.Message.Models.Publishing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Squid.Core.Services.Publishing.Inspectors;

/// <summary>
/// The author's own say: <c>recipe</c>, <c>run.port</c>, <c>run.command</c> and <c>health.path</c> from <c>squid.yaml</c>, recorded as given. Anything missing is simply
/// absent; a file that is not valid YAML, is not a mapping, or is nested or long past the limits is recorded in <see cref="PackageFactKeys.SquidError"/>, never thrown.
/// The file is read as a stream of parser events, never as an object graph: a document nested thousands of levels deep would overflow the stack of a recursive reader,
/// and a stack overflow cannot be caught. Only the four scalars above are looked at; everything else is skipped.
/// </summary>
public sealed class SquidYamlInspector : IPackageInspector
{
    public const int MaxDepth = 32;

    public const int MaxEvents = 10_000;

    private static readonly Dictionary<string, string> Wanted = new(StringComparer.Ordinal)
    {
        ["recipe"] = PackageFactKeys.SquidRecipe,
        ["run.port"] = PackageFactKeys.SquidRunPort,
        ["run.command"] = PackageFactKeys.SquidRunCommand,
        ["health.path"] = PackageFactKeys.SquidHealthPath
    };

    public IReadOnlyCollection<string> FilesToRead { get; } = ["squid.yaml"];

    public async Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
    {
        var text = await context.TryReadTextAsync("squid.yaml", ct).ConfigureAwait(false);

        if (text != null) Record(context, text);
    }

    private static void Record(PackageInspectionContext context, string text)
    {
        try
        {
            foreach (var (key, value) in Walk(text)) context.Set(key, value);
        }
        catch (Exception ex) when (ex is YamlException or SquidYamlLimitException)
        {
            context.Set(PackageFactKeys.SquidError, ex.Message);
        }
    }

    // Yields each wanted scalar as it is met. Throws when the document is not a mapping, or passes a limit.
    private static IEnumerable<(string Fact, string Value)> Walk(string text)
    {
        var parser = new Parser(new StringReader(text));
        var frames = new List<Frame>();
        var events = 0;

        while (parser.MoveNext())
        {
            if (++events > MaxEvents)
                throw new SquidYamlLimitException($"squid.yaml has more than {MaxEvents} YAML events");

            var current = parser.Current;

            if (current is DocumentEnd) yield break;

            if (current is MappingStart or SequenceStart) Open(frames, current is MappingStart);
            else if (current is MappingEnd or SequenceEnd) Close(frames);
            else if (current is Scalar or AnchorAlias && frames.Count == 0) throw new SquidYamlLimitException("squid.yaml must be a mapping of settings");
            else if (current is Scalar scalar && Wanted.TryGetValue(Path(frames, scalar, out var isValue), out var fact) && isValue) yield return (fact, scalar.Value);

            if (current is Scalar or AnchorAlias) Advance(frames);
        }
    }

    private static void Open(List<Frame> frames, bool isMapping)
    {
        if (frames.Count == 0 && !isMapping)
            throw new SquidYamlLimitException("squid.yaml must be a mapping of settings");

        if (frames.Count >= MaxDepth)
            throw new SquidYamlLimitException($"squid.yaml is nested deeper than {MaxDepth} levels");

        frames.Add(new Frame(isMapping));
    }

    private static void Close(List<Frame> frames)
    {
        frames.RemoveAt(frames.Count - 1);

        Advance(frames);
    }

    // A finished value (scalar or nested collection) in a mapping means the next scalar there is a key again.
    private static void Advance(List<Frame> frames)
    {
        if (frames.Count > 0 && frames[^1].IsMapping) frames[^1].ExpectKey = !frames[^1].ExpectKey;
    }

    // The dotted path of a scalar met in value position of nested mappings, or "" when it is a key or sits anywhere under a sequence.
    private static string Path(List<Frame> frames, Scalar scalar, out bool isValue)
    {
        isValue = false;

        if (frames.Count == 0 || frames.Any(f => !f.IsMapping)) return string.Empty;

        var top = frames[^1];

        if (top.ExpectKey)
        {
            top.Key = scalar.Value;
            return string.Empty;
        }

        isValue = true;

        return string.Join('.', frames.Select(f => f.Key));
    }

    private sealed class Frame(bool isMapping)
    {
        public bool IsMapping { get; } = isMapping;

        public bool ExpectKey { get; set; } = true;

        public string Key { get; set; }
    }

    private sealed class SquidYamlLimitException(string message) : Exception(message);
}
