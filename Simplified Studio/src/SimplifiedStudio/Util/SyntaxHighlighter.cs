using System;
using System.Collections.Generic;

namespace SimplifiedStudio.Util;

/// <summary>What a stretch of text is, for colouring.</summary>
public enum SyntaxKind
{
    /// <summary>Structure — braces, angle brackets, commas, colons, whitespace.</summary>
    Punctuation,
    /// <summary>A JSON property name, or an XML attribute name.</summary>
    Key,
    /// <summary>A quoted string: a JSON value or an XML attribute value.</summary>
    String,
    Number,
    /// <summary>true, false or null.</summary>
    Keyword,
    /// <summary>An XML element name.</summary>
    Tag,
    Comment,
    /// <summary>XML character data between tags.</summary>
    Text,
}

/// <summary>One coloured stretch of the source text.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind);

/// <summary>
/// Shared machinery for the preview's scanners.
///
/// Both are scanners rather than parsers: the preview has to colour whatever it
/// is handed, including a dump that is truncated or malformed, and a parser
/// would give up on the whole document over one bad byte. Anything unrecognised
/// falls through to punctuation and simply renders uncoloured.
///
/// Adjacent stretches of the same kind are merged into one span. That is what
/// keeps the count down — punctuation and whitespace are most of a pretty-
/// printed document, and one span per token would mean several times the text
/// runs for the same file.
/// </summary>
public static class SyntaxScanner
{
    /// <summary>Extend the previous span when the kind matches, rather than adding another.</summary>
    internal static void Append(List<SyntaxSpan> spans, int start, int length, SyntaxKind kind)
    {
        if (length <= 0) return;
        if (spans.Count > 0)
        {
            var last = spans[^1];
            if (last.Kind == kind && last.Start + last.Length == start)
            {
                spans[^1] = last with { Length = last.Length + length };
                return;
            }
        }
        spans.Add(new SyntaxSpan(start, length, kind));
    }

    /// <summary>Index just past the closing quote, honouring backslash escapes.</summary>
    internal static int SkipQuoted(string text, int i)
    {
        var quote = text[i];
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }   // an escaped quote isn't the end
            if (text[i] == quote) return i + 1;
            i++;
        }
        return i;   // unterminated — colour to the end rather than failing
    }

    internal static bool Matches(string text, int i, string word)
        => i + word.Length <= text.Length && string.CompareOrdinal(text, i, word, 0, word.Length) == 0;

    /// <summary>Index just past <paramref name="terminator"/>, or the end if it never comes.</summary>
    internal static int SkipUntil(string text, int i, string terminator)
    {
        var at = text.IndexOf(terminator, i, StringComparison.Ordinal);
        return at < 0 ? text.Length : at + terminator.Length;
    }
}

/// <summary>Splits JSON into coloured spans for the preview.</summary>
public static class JsonHighlighter
{
    public static List<SyntaxSpan> Scan(string text)
    {
        var spans = new List<SyntaxSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            SyntaxKind kind;

            var c = text[i];
            if (c == '"')
            {
                i = SyntaxScanner.SkipQuoted(text, i);
                // A string is a key only if the next thing that isn't space is a
                // colon. That check is the whole difference between a property
                // name and a string value.
                kind = NextNonSpace(text, i) == ':' ? SyntaxKind.Key : SyntaxKind.String;
            }
            else if (c == '-' || (c >= '0' && c <= '9'))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
                kind = SyntaxKind.Number;
            }
            else if (SyntaxScanner.Matches(text, i, "true") || SyntaxScanner.Matches(text, i, "null"))
            {
                i += 4;
                kind = SyntaxKind.Keyword;
            }
            else if (SyntaxScanner.Matches(text, i, "false"))
            {
                i += 5;
                kind = SyntaxKind.Keyword;
            }
            else
            {
                i++;
                kind = SyntaxKind.Punctuation;
            }

            SyntaxScanner.Append(spans, start, i - start, kind);
        }

        return spans;
    }

    private static char NextNonSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length ? text[i] : '\0';
    }
}

/// <summary>
/// Splits XML into coloured spans for the preview.
///
/// Everything outside a tag is character data, which is what the reader is
/// usually after in these files, so it takes the primary text colour while the
/// markup around it recedes.
/// </summary>
public static class XmlHighlighter
{
    public static List<SyntaxSpan> Scan(string text)
    {
        var spans = new List<SyntaxSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                // Character data, up to the next tag.
                var start = i;
                while (i < text.Length && text[i] != '<') i++;
                // Whitespace between tags is indentation, not content — colouring
                // it as text would tint every blank run in the document.
                SyntaxScanner.Append(spans, start, i - start,
                    IsBlank(text, start, i) ? SyntaxKind.Punctuation : SyntaxKind.Text);
                continue;
            }

            if (SyntaxScanner.Matches(text, i, "<!--"))
            {
                var end = SyntaxScanner.SkipUntil(text, i, "-->");
                SyntaxScanner.Append(spans, i, end - i, SyntaxKind.Comment);
                i = end;
                continue;
            }

            if (SyntaxScanner.Matches(text, i, "<![CDATA["))
            {
                var end = SyntaxScanner.SkipUntil(text, i, "]]>");
                SyntaxScanner.Append(spans, i, end - i, SyntaxKind.Text);
                i = end;
                continue;
            }

            i = ScanTag(text, i, spans);
        }

        return spans;
    }

    /// <summary>Colour one tag — name, then attribute name/value pairs — and return the index past it.</summary>
    private static int ScanTag(string text, int i, List<SyntaxSpan> spans)
    {
        var open = i;
        i++;                                              // '<'
        if (i < text.Length && (text[i] == '/' || text[i] == '?' || text[i] == '!')) i++;
        SyntaxScanner.Append(spans, open, i - open, SyntaxKind.Punctuation);

        // Element name.
        var nameStart = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '/' or '?')) i++;
        SyntaxScanner.Append(spans, nameStart, i - nameStart, SyntaxKind.Tag);

        // Attributes, until the tag closes.
        while (i < text.Length && text[i] != '>')
        {
            if (char.IsWhiteSpace(text[i]) || text[i] is '/' or '?' or '=')
            {
                SyntaxScanner.Append(spans, i, 1, SyntaxKind.Punctuation);
                i++;
                continue;
            }

            if (text[i] is '"' or '\'')
            {
                var valueStart = i;
                i = SyntaxScanner.SkipQuoted(text, i);
                // A quoted value can contain '>', which is why the tag can't just
                // be scanned to the next angle bracket.
                SyntaxScanner.Append(spans, valueStart, i - valueStart, SyntaxKind.String);
                continue;
            }

            var attrStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>' or '/' or '?')) i++;
            if (i == attrStart) i++;   // guard: never stall on an unexpected character
            SyntaxScanner.Append(spans, attrStart, i - attrStart, SyntaxKind.Key);
        }

        if (i < text.Length)
        {
            SyntaxScanner.Append(spans, i, 1, SyntaxKind.Punctuation);   // '>'
            i++;
        }
        return i;
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
}
