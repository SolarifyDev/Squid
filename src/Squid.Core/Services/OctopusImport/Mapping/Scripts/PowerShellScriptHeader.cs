namespace Squid.Core.Services.OctopusImport.Mapping.Scripts;

/// <summary>
/// Detects a PowerShell script whose first statement PowerShell requires to come first — <c>using</c>
/// statements, a <c>param()</c> block (optionally preceded by attributes such as <c>[CmdletBinding()]</c>),
/// or top-level named blocks (<c>begin</c>/<c>process</c>/<c>end</c>/<c>dynamicparam</c>/<c>clean</c>). Nothing can
/// be prepended to such a script without a parse error. Leading whitespace, a byte-order mark, line comments
/// (including <c>#requires</c>) and block comments are skipped; brackets and quotes inside attributes are
/// balanced, so <c>[OutputType([string[]])]</c> or <c>[Attr(']')]</c> is one attribute, while a type cast
/// such as <c>[string]$x = 1</c> is an ordinary statement.
/// </summary>
internal static class PowerShellScriptHeader
{
    private const char ByteOrderMark = '﻿';

    private static readonly string[] NamedBlocks = ["begin", "process", "end", "dynamicparam", "clean"];
    private static readonly string[] UsingKinds = ["namespace", "module", "assembly"];

    public static bool MustStayFirst(string script)
    {
        if (string.IsNullOrEmpty(script)) return false;

        var position = SkipAttributes(script, SkipTrivia(script, 0));

        return StartsWithParamBlock(script, position) || StartsWithUsing(script, position) || StartsWithNamedBlock(script, position);
    }

    private static int SkipTrivia(string script, int position)
    {
        while (position < script.Length)
        {
            if (char.IsWhiteSpace(script[position]) || script[position] == ByteOrderMark) position++;
            else if (string.CompareOrdinal(script, position, "<#", 0, 2) == 0) position = EndOfBlockComment(script, position);
            else if (script[position] == '#') position = EndOfLine(script, position);
            else break;
        }

        return position;
    }

    private static int SkipAttributes(string script, int position)
    {
        while (position < script.Length && script[position] == '[')
        {
            var end = EndOfBracketed(script, position);

            if (end < 0) break;

            position = SkipTrivia(script, end);
        }

        return position;
    }

    private static bool StartsWithParamBlock(string script, int position)
        => StartsWithKeyword(script, position, "param") && NextNonWhitespace(script, position + "param".Length) == '(';

    private static bool StartsWithUsing(string script, int position)
    {
        if (!StartsWithKeyword(script, position, "using")) return false;

        var kindStart = SkipWhitespace(script, position + "using".Length);

        return kindStart > position + "using".Length && UsingKinds.Any(kind => StartsWithKeyword(script, kindStart, kind));
    }

    private static bool StartsWithNamedBlock(string script, int position)
        => NamedBlocks.Any(block => StartsWithKeyword(script, position, block) && NextNonWhitespace(script, position + block.Length) == '{');

    private static bool StartsWithKeyword(string script, int position, string keyword)
    {
        if (position + keyword.Length > script.Length) return false;

        if (string.Compare(script, position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;

        var next = position + keyword.Length;

        return next == script.Length || !IsIdentifierChar(script[next]);
    }

    private static int EndOfBracketed(string script, int position)
    {
        var depth = 0;

        for (var i = position; i < script.Length; i++)
        {
            var current = script[i];

            if (current == '[') depth++;
            else if (current == ']' && --depth == 0) return i + 1;
            else if (current == '\'') i = EndOfSingleQuoted(script, i);
            else if (current == '"') i = EndOfDoubleQuoted(script, i);

            if (i < 0) return -1;
        }

        return -1;
    }

    // PowerShell escapes a quote inside a single-quoted string by doubling it.
    private static int EndOfSingleQuoted(string script, int position)
    {
        for (var i = position + 1; i < script.Length; i++)
        {
            if (script[i] != '\'') continue;

            if (i + 1 < script.Length && script[i + 1] == '\'') { i++; continue; }

            return i;
        }

        return -1;
    }

    // Inside a double-quoted string a backtick escapes the next character, and "" is an escaped quote.
    private static int EndOfDoubleQuoted(string script, int position)
    {
        for (var i = position + 1; i < script.Length; i++)
        {
            if (script[i] == '`') { i++; continue; }

            if (script[i] != '"') continue;

            if (i + 1 < script.Length && script[i + 1] == '"') { i++; continue; }

            return i;
        }

        return -1;
    }

    private static int EndOfBlockComment(string script, int position)
    {
        var end = script.IndexOf("#>", position + 2, StringComparison.Ordinal);

        return end < 0 ? script.Length : end + 2;
    }

    private static int EndOfLine(string script, int position)
    {
        var end = script.IndexOfAny(['\r', '\n'], position);

        return end < 0 ? script.Length : end;
    }

    private static int SkipWhitespace(string script, int position)
    {
        while (position < script.Length && char.IsWhiteSpace(script[position])) position++;

        return position;
    }

    private static char NextNonWhitespace(string script, int position)
    {
        var next = SkipWhitespace(script, position);

        return next < script.Length ? script[next] : '\0';
    }

    private static bool IsIdentifierChar(char value) => char.IsLetterOrDigit(value) || value is '_' or '-' or ':';
}
