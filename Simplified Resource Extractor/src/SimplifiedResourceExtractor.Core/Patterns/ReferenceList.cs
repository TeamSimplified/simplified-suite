using System.Text;
using System.Text.RegularExpressions;

namespace SimplifiedResourceExtractor.Core.Patterns;

/// <summary>
/// The rules that decide which resources are wanted, as written in
/// <c>reference.txt</c>.
///
/// One rule per line, and a resource is wanted when any line matches it. Within
/// a line:
///
/// <list type="bullet">
///   <item><c> + </c> — both, in any order: <c>ferrari + livery</c></item>
///   <item><c> | </c> — either: <c>boots | gloves</c></item>
///   <item><c> - </c> — but not: <c>ferrari - boots</c></item>
///   <item><c>( )</c> — grouping: <c>(boots | gloves) + ferrari</c></item>
///   <item><c>?</c> — any one digit: <c>livery_v?_d</c></item>
///   <item><c>#</c> — a comment, when it opens the line</item>
/// </list>
///
/// Terms are plain substrings of the resource's <c>eaid://…</c> identifier and
/// are matched case-sensitively, so a whole identifier is itself a valid rule —
/// which is how the list of texarray overrides in the wild is written.
///
/// The operators need their spaces. That is not decoration: an earlier version
/// of this tool split on a bare <c>-</c>, so <c>red-bull</c> parsed as "include
/// red, exclude bull" and quietly matched the wrong half of the grid. Requiring
/// <c> - </c> is what makes a hyphen inside a term ordinary.
///
/// Grouping nests properly, which the tool this grew out of did not do: it read
/// a line as one flat sum of products, so a bracketed <c>|</c> sitting inside a
/// <c>+</c> came out as a term with brackets and spaces in it and matched
/// nothing at all. Nothing that worked before parses differently now — an
/// identifier has no spaces in it, so a "term" carrying an operator was already
/// a term that could never match.
/// </summary>
public sealed class ReferenceList
{
    private readonly Condition[] _conditions;

    /// <summary>Lines that were read as rules.</summary>
    public int RuleCount => _conditions.Length;

    /// <summary>
    /// Lines that looked like rules and produced none — an exclude with nothing
    /// to exclude from, say. The Python this grew out of dropped them in
    /// silence, which makes a typo look like "no matches in the game".
    /// </summary>
    public IReadOnlyList<string> Ignored { get; }

    private ReferenceList(Condition[] conditions, IReadOnlyList<string> ignored)
    {
        _conditions = conditions;
        Ignored = ignored;
    }

    /// <summary>Is this resource wanted?</summary>
    public bool Matches(string identifier)
    {
        // Deliberately a loop rather than LINQ: this runs once per resource, and
        // a full install holds 2.3 million of them.
        foreach (var condition in _conditions)
        {
            if (condition.Matches(identifier)) return true;
        }

        return false;
    }

    /// <summary>Read the rules from text.</summary>
    public static ReferenceList Parse(string text)
    {
        var conditions = new List<Condition>();
        var ignored = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // A line naming a resource outright is that resource, not a pattern
            // that happens to look like one. Nothing is parsed out of it and
            // nothing but that identifier matches it.
            //
            // This is what makes a list pulled off an archive behave: as
            // substrings those identifiers over-match, since every one of them
            // is a prefix of its own .image and of anything else built on the
            // same name. It also stops a ? or a stray operator inside an eaid
            // from being read as syntax.
            if (line.Contains(IdentifierScheme, StringComparison.OrdinalIgnoreCase))
            {
                conditions.Add(new Condition(new Exactly(line), []));
                continue;
            }

            // The first part is what to include; everything after a top-level
            // " - " is a separate reason to reject.
            var parts = SplitTopLevel(line, " - ");
            if (parts.Count == 0) { ignored.Add(line); continue; }

            var include = ParseExpression(parts[0]);
            if (include is null) { ignored.Add(line); continue; }

            var exclude = parts.Skip(1)
                .Select(ParseExpression)
                .OfType<INode>()
                .ToArray();

            conditions.Add(new Condition(include, exclude));
        }

        return new ReferenceList(conditions.ToArray(), ignored);
    }

    /// <summary>
    /// Read the rules from a file, creating it with a worked example when it
    /// isn't there yet. A first run should open on something to edit rather
    /// than on an error.
    /// </summary>
    public static ReferenceList Load(string path)
    {
        if (!File.Exists(path))
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, DefaultText, Encoding.UTF8);
        }

        return Parse(File.ReadAllText(path));
    }

    /// <summary>What marks a line out as naming one resource rather than describing many.</summary>
    public const string IdentifierScheme = "eaid://";

    /// <summary>What a new <c>reference.txt</c> starts as.</summary>
    public const string DefaultText = """
        # One rule per line. A resource is taken when any line matches it.
        #
        #   ferrari + livery      both, in any order
        #   boots | gloves        either one
        #   ferrari - boots       ferrari, but not boots
        #   livery_v?_d           ? stands for any one digit
        #
        # The spaces around + | - are part of them, so a hyphen inside a term is
        # just a hyphen. Terms match anywhere in the resource's eaid.
        #
        # A line containing eaid:// is that one resource exactly. None of the
        # above applies to it - which is what "List from ERP" writes.

        """;

    /// <summary>
    /// Add identifiers to a rule list, under a heading saying where they came
    /// from, skipping any the list already has.
    ///
    /// Appended rather than substituted: the archive is a source of rules to
    /// pick over, not the whole answer, and somebody has usually written
    /// something above it already.
    /// </summary>
    public static string Append(string rulesText, IEnumerable<string> identifiers, string source)
    {
        var existing = rulesText
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var added = identifiers
            .Select(i => i.Trim())
            .Where(i => i.Length > 0 && existing.Add(i))
            .ToList();

        if (added.Count == 0) return rulesText;

        var text = new StringBuilder(rulesText.TrimEnd());
        if (text.Length > 0) text.AppendLine().AppendLine();

        text.Append("# ").Append(added.Count).Append(added.Count == 1 ? " resource from " : " resources from ")
            .AppendLine(source);
        foreach (var identifier in added) text.AppendLine(identifier);

        return text.ToString();
    }

    /// <summary>How many of these are already in the list, for reporting.</summary>
    public static int CountAlreadyPresent(string rulesText, IEnumerable<string> identifiers)
    {
        var existing = rulesText
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return identifiers.Count(i => existing.Contains(i.Trim()));
    }

    // ── Parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// An expression: alternatives separated by <c> | </c>, each of them
    /// requirements separated by <c> + </c>, each of those either a term or a
    /// bracketed expression of its own. Null when there is nothing in it.
    /// </summary>
    private static INode? ParseExpression(string expression)
    {
        var clauses = SplitTopLevel(StripOuterParens(expression), " | ")
            .Select(ParseConjunction)
            .OfType<INode>()
            .ToArray();

        return clauses.Length switch
        {
            0 => null,
            1 => clauses[0],
            _ => new AnyOf(clauses),
        };
    }

    private static INode? ParseConjunction(string clause)
    {
        var parts = SplitTopLevel(StripOuterParens(clause), " + ")
            .Select(ParseAtom)
            .OfType<INode>()
            .ToArray();

        return parts.Length switch
        {
            0 => null,
            1 => parts[0],
            _ => new AllOf(parts),
        };
    }

    /// <summary>
    /// A term, or a bracketed expression. Brackets are only brackets when they
    /// wrap the whole piece: <c>(a)+(b)</c> has no operator the split above
    /// could see, so it stays one term — the operators need their spaces, and
    /// this is what that means at the leaves.
    /// </summary>
    private static INode? ParseAtom(string atom)
    {
        atom = atom.Trim();
        if (atom.Length == 0) return null;

        var inner = StripOuterParens(atom);
        return inner == atom ? Term.For(atom) : ParseExpression(inner);
    }

    /// <summary>
    /// Split on <paramref name="separator"/>, but only where it sits outside
    /// any parentheses. Empty pieces are dropped, so a trailing operator is a
    /// no-op rather than an empty term that matches everything.
    /// </summary>
    private static List<string> SplitTopLevel(string s, string separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var last = 0;

        for (var i = 0; i < s.Length;)
        {
            switch (s[i])
            {
                case '(':
                    ++depth;
                    ++i;
                    continue;
                case ')':
                    --depth;
                    ++i;
                    continue;
            }

            if (depth == 0 && i + separator.Length <= s.Length
                && string.CompareOrdinal(s, i, separator, 0, separator.Length) == 0)
            {
                Add(s[last..i]);
                i += separator.Length;
                last = i;
                continue;
            }

            ++i;
        }

        Add(s[last..]);
        return parts;

        void Add(string piece)
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > 0) parts.Add(trimmed);
        }
    }

    /// <summary>
    /// Peel off parentheses that wrap the whole expression. Only when they
    /// really do wrap it — <c>(a) + (b)</c> opens and closes twice, so its
    /// first bracket is not the partner of its last.
    /// </summary>
    private static string StripOuterParens(string s)
    {
        s = s.Trim();

        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            var depth = 0;
            var wraps = true;

            for (var i = 0; i < s.Length; ++i)
            {
                if (s[i] == '(') ++depth;
                else if (s[i] == ')' && --depth == 0 && i != s.Length - 1)
                {
                    wraps = false;
                    break;
                }
            }

            if (!wraps) break;
            s = s[1..^1].Trim();
        }

        return s;
    }

    // ── The parsed shape ──────────────────────────────────────────────

    /// <summary>One line: something to match, and any number of reasons not to.</summary>
    private sealed class Condition(INode include, INode[] exclude)
    {
        public bool Matches(string identifier)
        {
            if (!include.Matches(identifier)) return false;

            foreach (var reason in exclude)
            {
                if (reason.Matches(identifier)) return false;
            }

            return true;
        }
    }

    /// <summary>A piece of a rule, which can be asked about an identifier.</summary>
    private interface INode
    {
        bool Matches(string identifier);
    }

    /// <summary>One named resource, and only that one.</summary>
    private sealed class Exactly(string identifier) : INode
    {
        public bool Matches(string candidate) =>
            string.Equals(candidate, identifier, StringComparison.Ordinal);
    }

    /// <summary>Every part, for <c> + </c>.</summary>
    private sealed class AllOf(INode[] parts) : INode
    {
        public bool Matches(string identifier)
        {
            foreach (var part in parts)
            {
                if (!part.Matches(identifier)) return false;
            }

            return true;
        }
    }

    /// <summary>Any part, for <c> | </c>.</summary>
    private sealed class AnyOf(INode[] parts) : INode
    {
        public bool Matches(string identifier)
        {
            foreach (var part in parts)
            {
                if (part.Matches(identifier)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// One piece of text to look for. Plain substrings are the overwhelming
    /// majority and stay substrings; only a term carrying <c>?</c> pays for a
    /// regex, and that one is compiled because it is asked the same question
    /// millions of times in a scan.
    /// </summary>
    private sealed class Term : INode
    {
        private readonly string _text;
        private readonly Regex? _pattern;

        private Term(string text, Regex? pattern)
        {
            _text = text;
            _pattern = pattern;
        }

        public static Term For(string text) => text.Contains('?')
            ? new Term(text, new Regex(Regex.Escape(text).Replace(@"\?", "[0-9]"),
                                       RegexOptions.Compiled | RegexOptions.CultureInvariant))
            : new Term(text, null);

        public bool Matches(string identifier) => _pattern is null
            ? identifier.Contains(_text, StringComparison.Ordinal)
            : _pattern.IsMatch(identifier);
    }
}
