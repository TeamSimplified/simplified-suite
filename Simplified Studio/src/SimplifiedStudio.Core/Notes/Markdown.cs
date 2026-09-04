using System.Text;

namespace SimplifiedStudio.Core.Notes;

/// <summary>
/// Just enough Markdown for a mod maker's notes.
///
/// Written rather than taken off the shelf for one reason: a task list has to
/// be clickable. Every renderer worth using draws <c>- [ ]</c> as a checkbox
/// that cannot be ticked, which for a to-do list means editing brackets by hand
/// in the source pane — so the parser records the source line each task came
/// from and the renderer writes the tick back into it.
///
/// The subset is headings, paragraphs, bullets and task lists, numbered lists,
/// fenced and indented code, quotes and rules, with bold, italic, strike,
/// inline code and links inside them. Anything else — tables, footnotes,
/// reference links, HTML — comes out as the plain text it was written as,
/// which is worth saying out loud: this degrades, it doesn't break.
/// </summary>
public static class Markdown
{
    // ── What a document is made of ──────────────────────────────────────────

    public abstract record Block;

    public sealed record Heading(int Level, IReadOnlyList<Span> Text) : Block;
    public sealed record Paragraph(IReadOnlyList<Span> Text) : Block;
    public sealed record Rule : Block;
    public sealed record CodeBlock(string Text) : Block;
    public sealed record Quote(IReadOnlyList<Span> Text) : Block;

    /// <summary>
    /// One line of a list.
    /// </summary>
    /// <param name="Marker">
    /// What stands at the left: a bullet, or the number the author wrote. Their
    /// numbering is kept as typed rather than renumbered — a list that starts at
    /// 3 was probably meant to.
    /// </param>
    /// <param name="Checked">
    /// Null for an ordinary list item; true or false for a task. The difference
    /// between "no checkbox" and "an unticked one".
    /// </param>
    /// <param name="SourceLine">
    /// Which line of the source this came from, so a tick can be written back
    /// to exactly the line the reader clicked.
    /// </param>
    public sealed record ListItem(
        int Indent, string? Marker, bool? Checked, IReadOnlyList<Span> Text, int SourceLine) : Block;

    /// <summary>A run of text, and how it should look.</summary>
    public sealed record Span(string Text, bool Bold = false, bool Italic = false,
                              bool Code = false, bool Strike = false, string? Url = null);

    // ── Parsing ─────────────────────────────────────────────────────────────

    public static List<Block> Parse(string source)
    {
        var blocks = new List<Block>();
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new Paragraph(ParseInline(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Fenced code runs to its closing fence, or to the end of the file
            // if the author never wrote one.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new StringBuilder();
                i++;
                for (; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                    code.AppendLine(lines[i]);
                }
                blocks.Add(new CodeBlock(code.ToString().TrimEnd('\n')));
                continue;
            }

            if (trimmed.Length == 0) { FlushParagraph(); continue; }

            if (IsRule(trimmed)) { FlushParagraph(); blocks.Add(new Rule()); continue; }

            if (TryHeading(trimmed, out var level, out var headingText))
            {
                FlushParagraph();
                blocks.Add(new Heading(level, ParseInline(headingText)));
                continue;
            }

            if (TryListItem(line, i, out var item))
            {
                FlushParagraph();
                blocks.Add(item!);
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                FlushParagraph();
                blocks.Add(new Quote(ParseInline(trimmed.Length > 1 ? trimmed[2..] : "")));
                continue;
            }

            // Four spaces or a tab is a code block, but only where a paragraph
            // isn't already running — otherwise it's a wrapped line.
            if (paragraph.Count == 0 && (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)))
            {
                blocks.Add(new CodeBlock(line.StartsWith("\t", StringComparison.Ordinal) ? line[1..] : line[4..]));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        var c = trimmed[0];
        return (c == '-' || c == '*' || c == '_') && trimmed.All(x => x == c || x == ' ');
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = "";
        while (level < trimmed.Length && trimmed[level] == '#') level++;

        if (level is 0 or > 6) return false;
        if (level < trimmed.Length && trimmed[level] != ' ') return false;   // "#tag" is not a heading

        text = trimmed[level..].Trim();
        return true;
    }

    /// <summary>
    /// A bullet, a numbered item, or a task. The indent is counted in spaces
    /// before the marker, two to a level, so a sub-item under a bullet reads as
    /// one nested step rather than an arbitrary margin.
    /// </summary>
    private static bool TryListItem(string line, int lineNumber, out ListItem? item)
    {
        item = null;

        var spaces = 0;
        while (spaces < line.Length && (line[spaces] == ' ' || line[spaces] == '\t')) spaces++;
        var rest = line[spaces..];
        if (rest.Length < 2) return false;

        string? marker = null;
        string body;

        if ((rest[0] is '-' or '*' or '+') && rest[1] == ' ')
        {
            marker = null;                  // a bullet the renderer draws itself
            body = rest[2..];
        }
        else
        {
            // "12. " — a number, a dot or bracket, then a space.
            var digits = 0;
            while (digits < rest.Length && char.IsDigit(rest[digits])) digits++;
            if (digits == 0 || digits + 1 >= rest.Length) return false;
            if (rest[digits] is not ('.' or ')') || rest[digits + 1] != ' ') return false;

            marker = rest[..digits] + rest[digits];
            body = rest[(digits + 2)..];
        }

        // "[ ] " or "[x] " right after the marker makes it a task.
        bool? ticked = null;
        if (body.Length >= 3 && body[0] == '[' && body[2] == ']' &&
            (body.Length == 3 || body[3] == ' '))
        {
            var box = body[1];
            if (box is ' ') { ticked = false; body = body.Length > 3 ? body[4..] : ""; }
            else if (box is 'x' or 'X') { ticked = true; body = body.Length > 3 ? body[4..] : ""; }
        }

        var tabsCounted = line[..spaces].Count(c => c == '\t');
        var indent = (spaces - tabsCounted) / 2 + tabsCounted;

        item = new ListItem(indent, marker, ticked, ParseInline(body), lineNumber);
        return true;
    }

    // ── Inline ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Split a line into styled runs.
    ///
    /// One pass, left to right, closing each marker at the next matching one.
    /// An opener with no partner stays as the literal character it is, which is
    /// what someone writing <c>2 * 3</c> in a note means by it.
    /// </summary>
    public static List<Span> ParseInline(string text)
    {
        var spans = new List<Span>();
        var run = new StringBuilder();
        bool bold = false, italic = false, strike = false;

        void Flush()
        {
            if (run.Length == 0) return;
            spans.Add(new Span(run.ToString(), bold, italic, false, strike));
            run.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            // Inline code wins over everything: what's inside is literal.
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    spans.Add(new Span(text[(i + 1)..close], Code: true));
                    i = close;
                    continue;
                }
            }

            // [text](url)
            if (text[i] == '[')
            {
                var closeText = text.IndexOf(']', i + 1);
                if (closeText > i && closeText + 1 < text.Length && text[closeText + 1] == '(')
                {
                    var closeUrl = text.IndexOf(')', closeText + 2);
                    if (closeUrl > closeText)
                    {
                        Flush();
                        spans.Add(new Span(text[(i + 1)..closeText], bold, italic, false, strike,
                                           text[(closeText + 2)..closeUrl]));
                        i = closeUrl;
                        continue;
                    }
                }
            }

            // A marker closing something already open needs no partner of its
            // own. Asking for one looks past the end of the styled run, finds
            // nothing, and leaves the closing marker sitting in the text with
            // the styling still switched on.
            if (Opens(text, i, "**") || Opens(text, i, "__"))
            {
                if (bold || HasCloser(text, i + 2, text.Substring(i, 2)))
                { Flush(); bold = !bold; i++; continue; }
            }

            if (Opens(text, i, "~~"))
            {
                if (strike || HasCloser(text, i + 2, "~~")) { Flush(); strike = !strike; i++; continue; }
            }

            if ((text[i] == '*' || text[i] == '_') && !Opens(text, i, "**") && !Opens(text, i, "__"))
            {
                if (italic || HasCloser(text, i + 1, text[i].ToString()))
                { Flush(); italic = !italic; continue; }
            }

            run.Append(text[i]);
        }

        Flush();
        return spans.Count > 0 ? spans : [new Span("")];
    }

    private static bool Opens(string text, int i, string marker) =>
        i + marker.Length <= text.Length && text.AsSpan(i, marker.Length).SequenceEqual(marker);

    /// <summary>
    /// Whether a marker opened here is ever closed. Without this, a lone
    /// asterisk would turn the rest of the line italic.
    /// </summary>
    private static bool HasCloser(string text, int from, string marker) =>
        from <= text.Length && text.IndexOf(marker, from, StringComparison.Ordinal) >= 0;

    // ── Writing a tick back ─────────────────────────────────────────────────

    /// <summary>
    /// Tick or untick the task on one line of the source, leaving everything
    /// else — indent, marker, wording — exactly as it was.
    ///
    /// Returns the source unchanged when that line isn't a task after all,
    /// which can happen if the text was edited between rendering and clicking.
    /// </summary>
    public static string SetTask(string source, int lineNumber, bool ticked)
    {
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lineNumber < 0 || lineNumber >= lines.Length) return source;

        var line = lines[lineNumber];
        var box = line.IndexOf('[');
        if (box < 0 || box + 2 >= line.Length || line[box + 2] != ']') return source;

        lines[lineNumber] = line[..(box + 1)] + (ticked ? "x" : " ") + line[(box + 2)..];
        return string.Join(newline, lines);
    }
}
