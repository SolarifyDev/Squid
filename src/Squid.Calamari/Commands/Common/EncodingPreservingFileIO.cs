using System.Text;

namespace Squid.Calamari.Commands.Common;

/// <summary>
/// Shared file-IO primitives used by every step that rewrites operator-
/// nominated text files (SubstituteInFilesStep / StructuredConfigVariablesStep
/// / ConfigurationTransformsStep).
///
/// <para><b>Why centralise</b>: each rewriter MUST behave identically on the
/// two cross-cutting concerns where operators silently get burned by sloppy
/// implementations:
/// <list type="bullet">
///   <item><b>UTF-8 BOM preservation</b> — Visual Studio writes appsettings.json
///         / web.config with a BOM by default. .NET's <c>File.ReadAllText</c>
///         silently strips it; <c>File.WriteAllText</c> never re-adds it.
///         A rewrite would change the byte content even when the logical text
///         is identical, polluting diffs and confusing operators chasing a
///         "what changed?" question.</item>
///   <item><b>Atomic write</b> — write to a sibling temp path, fsync via
///         <c>File.Move(... overwrite: true)</c>. If the writer dies mid-way
///         (disk full, kill -9, OS panic) the operator's original file stays
///         intact; the worst case is a leftover <c>.calamari-tmp</c> sibling
///         that cleanup can mop up.</item>
/// </list></para>
///
/// <para><b>Scope</b>: text files only. Binary or encoding-detected-as-binary
/// files are not this class's concern — the caller skips them upstream.</para>
/// </summary>
internal static class EncodingPreservingFileIO
{
    /// <summary>
    /// Read file contents detecting the UTF-8 BOM so the caller can write it
    /// back exactly the same way. Returns the text content AND the encoding
    /// object to thread back to <see cref="WriteAllTextAtomic"/>.
    ///
    /// <para>UTF-8 with BOM and UTF-8 without BOM are different at the byte
    /// level; the <see cref="UTF8Encoding"/> instances returned here encode
    /// that distinction via <c>encoderShouldEmitUTF8Identifier</c>.</para>
    /// </summary>
    public static (string Text, Encoding Encoding) ReadAllTextPreservingEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // UTF-8 BOM: EF BB BF
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var withBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return (withBom.GetString(bytes, 3, bytes.Length - 3), withBom);
        }

        // No BOM — operator's text content; assume UTF-8.
        var noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return (noBom.GetString(bytes), noBom);
    }

    /// <summary>
    /// Atomic text write: render to a sibling temp file, then
    /// <c>File.Move(overwrite: true)</c> into place. Crash mid-write leaves
    /// the original file untouched.
    ///
    /// <para>Caller picks the <paramref name="encoding"/> — typically the
    /// encoding returned by <see cref="ReadAllTextPreservingEncoding"/> on
    /// the same path, so BOM is preserved on round-trip.</para>
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content, Encoding encoding)
    {
        var tempPath = path + TempSuffix;

        // Write to sibling temp first. If this throws (disk full / permission)
        // the original file is intact — the temp may exist but is junk we tag
        // with a known suffix so cleanup can find it.
        File.WriteAllText(tempPath, content, encoding);

        // File.Move with overwrite=true on .NET 9 is atomic on POSIX (rename(2))
        // and atomic-equivalent on Windows (MoveFileEx + MOVEFILE_REPLACE_EXISTING).
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Atomic byte write — same pattern, used by step that already has the
    /// bytes ready (e.g. XDT writes via <c>XmlDocument.Save</c> to the temp
    /// path directly and then renames).
    /// </summary>
    public static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        var tempPath = path + TempSuffix;
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Sibling-temp-file suffix. Public so cleanup tooling (and tests) can
    /// scan for leftover temps without hard-coding the suffix string.
    /// </summary>
    public const string TempSuffix = ".calamari-tmp";
}
