using System.Globalization;
using System.Text;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.ScriptExecution.Logging;

/// <summary>
/// On-disk format for sequenced logs:
///   {seq:D12}\t{iso8601}\t{source:O|E|D}\t{base64_text}\n
///
/// Fixed-width sequence number allows cheap seek-by-sequence.
/// Base64-encoded payload avoids ambiguity with tab/newline inside script output.
/// Each line is self-delimiting — a mid-write crash that truncates the final line
/// is detected by the parser (missing trailing newline) and discarded without
/// breaking earlier entries.
/// </summary>
internal static class SequencedLogFormat
{
    public const int SequenceWidth = 12;
    public const char FieldSeparator = '\t';
    public const char LineSeparator = '\n';

    public static string Encode(SequencedLogEntry entry)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Text));
        return string.Create(CultureInfo.InvariantCulture,
            $"{entry.Sequence.ToString($"D{SequenceWidth}", CultureInfo.InvariantCulture)}\t{entry.Occurred:O}\t{SourceToChar(entry.Source)}\t{b64}");
    }

    public static bool TryDecode(string line, out SequencedLogEntry entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(line)) return false;

        var parts = line.Split(FieldSeparator, 4);
        if (parts.Length != 4) return false;

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) return false;
        if (!DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurred)) return false;

        var source = CharToSource(parts[2]);
        if (source == null) return false;

        string text;
        try { text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])); }
        catch (FormatException) { return false; }

        entry = new SequencedLogEntry(seq, occurred, source.Value, text);
        return true;
    }

    private static char SourceToChar(ProcessOutputSource source) => source switch
    {
        ProcessOutputSource.StdOut => 'O',
        ProcessOutputSource.StdErr => 'E',
        ProcessOutputSource.Debug => 'D',
        _ => 'O'
    };

    private static ProcessOutputSource? CharToSource(string s) => s switch
    {
        "O" => ProcessOutputSource.StdOut,
        "E" => ProcessOutputSource.StdErr,
        "D" => ProcessOutputSource.Debug,
        _ => null
    };
}
