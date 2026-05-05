using System.Text;

namespace Wildcard;

// Cosmos DB NoSQL: native STARTSWITH/ENDSWITH/CONTAINS/STRINGEQUALS with ignoreCase boolean;
// LIKE uses T-SQL-style bracket-escape; REGEXMATCH for regex with "i" flag.
internal sealed class CosmosSqlRenderer(string parameterPrefix) : SqlRenderer(parameterPrefix)
{
    private static string EscapeLiteral(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            switch (ch)
            {
                case '%': sb.Append("[%]"); break;
                case '_': sb.Append("[_]"); break;
                case '[': sb.Append("[[]"); break;
                default:  sb.Append(ch); break;
            }
        return sb.ToString();
    }

    private static string BuildLikePattern(LikePart[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            switch (p)
            {
                case LikePart.AnySequence: sb.Append('%'); break;
                case LikePart.AnySingle s: sb.Append('_', s.Count); break;
                case LikePart.Literal l:   sb.Append(EscapeLiteral(l.Value)); break;
                default: throw new NotSupportedException($"Unknown LikePart {p.GetType().Name}");
            }
        }
        return sb.ToString();
    }

    private static string Bool(bool b) => b ? "true" : "false";

    protected override string RenderExact(string value, bool ignoreCase, string column) =>
        $"STRINGEQUALS({column}, {AddParam(value)}, {Bool(ignoreCase)})";

    protected override string RenderStartsWith(string prefix, bool ignoreCase, string column) =>
        $"STARTSWITH({column}, {AddParam(prefix)}, {Bool(ignoreCase)})";

    protected override string RenderEndsWith(string suffix, bool ignoreCase, string column) =>
        $"ENDSWITH({column}, {AddParam(suffix)}, {Bool(ignoreCase)})";

    protected override string RenderContains(string value, bool ignoreCase, string column) =>
        $"CONTAINS({column}, {AddParam(value)}, {Bool(ignoreCase)})";

    // Decompose into native STARTSWITH AND ENDSWITH so each side keeps its index tier
    // (precise index scan / expanded index scan) instead of dropping to a full LIKE scan.
    // The LENGTH guard prevents prefix and suffix from overlapping on short inputs —
    // e.g. pattern "ab*ab" on input "ab": STARTSWITH and ENDSWITH would both be true,
    // but WildcardPattern requires input.Length >= prefix.Length + suffix.Length.
    protected override string RenderStartsAndEndsWith(string prefix, string suffix, bool ignoreCase, string column)
    {
        var sw = RenderStartsWith(prefix, ignoreCase, column);
        var ew = RenderEndsWith(suffix, ignoreCase, column);
        var minLen = prefix.Length + suffix.Length;
        return $"({sw} AND {ew} AND LENGTH({column}) >= {minLen})";
    }

    protected override string RenderLike(LikePart[] parts, bool ignoreCase, string column)
    {
        var pattern = BuildLikePattern(parts);
        return ignoreCase
            ? $"LOWER({column}) LIKE {AddParam(pattern.ToLowerInvariant())}"
            : $"{column} LIKE {AddParam(pattern)}";
    }

    protected override string RenderRegex(string pattern, bool ignoreCase, string column) =>
        ignoreCase
            ? $"REGEXMATCH({column}, {AddParam(pattern)}, \"i\")"
            : $"REGEXMATCH({column}, {AddParam(pattern)})";
}
