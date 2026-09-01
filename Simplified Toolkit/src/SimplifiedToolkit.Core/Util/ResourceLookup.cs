using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;

namespace EgoEngineLibrary.Util;

/// <summary>
/// Flexible resource lookup that accepts:
///   - full identifier (eaid://...)
///   - resource filename (e.g. ferrari_decal.tga)
///   - glob with * / ?
///   - numeric index prefixed with # (e.g. #42)
/// Returns exactly one match or throws.
/// </summary>
public static class ResourceLookup
{
    public static ErpResource FindOne(ErpFile erp, string query)
    {
        var matches = Find(erp, query).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No resource matched: {query}"),
            _ => throw new InvalidOperationException(
                $"Ambiguous query '{query}' matched {matches.Count} resources. " +
                $"First few: {string.Join(", ", matches.Take(3).Select(r => r.Identifier))}"),
        };
    }

    public static IEnumerable<ErpResource> Find(ErpFile erp, string query)
    {
        if (string.IsNullOrEmpty(query)) return [];

        // #N — numeric index
        if (query.StartsWith('#') && int.TryParse(query[1..], out var idx))
        {
            if (idx < 0 || idx >= erp.Resources.Count)
                throw new ArgumentOutOfRangeException(nameof(query), $"Index {idx} out of range (0..{erp.Resources.Count - 1}).");
            return [erp.Resources[idx]];
        }

        // Exact identifier match — fast path
        var exact = erp.Resources.FirstOrDefault(r => string.Equals(r.Identifier, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return [exact];

        // Glob / wildcard match against identifier or filename
        if (query.Contains('*') || query.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(query)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);
            return erp.Resources.Where(r => rx.IsMatch(r.Identifier) || rx.IsMatch(r.FileName));
        }

        // Filename match (case-insensitive)
        return erp.Resources.Where(r => string.Equals(r.FileName, query, StringComparison.OrdinalIgnoreCase));
    }
}
