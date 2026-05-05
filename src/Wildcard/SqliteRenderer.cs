using System.Text;

namespace Wildcard;

// SQLite: each case mode uses its natural operator instead of forcing LOWER().
//   - case-sensitive  → GLOB (binary, byte-wise; matches StringComparison.Ordinal)
//   - case-insensitive → LIKE (ASCII case-folding by default; matches OrdinalIgnoreCase for ASCII)
//   - Exact CI         → "= ... COLLATE NOCASE" (ASCII CI, indexable on NOCASE columns)
//   - Regex           → REGEXP, requires the regexp extension to be registered on the connection
//
// Caveat: SQLite's default LIKE/NOCASE only fold the 26 ASCII letters; .NET's OrdinalIgnoreCase
// folds Unicode case more broadly. For mostly-ASCII glob patterns the two agree; users who match
// against Unicode-cased data should load an ICU-aware extension or pre-normalize.
internal sealed class SqliteRenderer(string parameterPrefix) : SqlRenderer(parameterPrefix)
{
    private const string LikeEscapeClause = @" ESCAPE '\'";

    private static string EscapeGlobLiteral(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            switch (ch)
            {
                case '*': sb.Append("[*]"); break;
                case '?': sb.Append("[?]"); break;
                case '[': sb.Append("[[]"); break;
                default:  sb.Append(ch); break;
            }
        return sb.ToString();
    }

    private static string EscapeLikeLiteral(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            switch (ch)
            {
                case '%':  sb.Append(@"\%"); break;
                case '_':  sb.Append(@"\_"); break;
                case '\\': sb.Append(@"\\"); break;
                default:   sb.Append(ch); break;
            }
        return sb.ToString();
    }

    private static string BuildGlobPattern(LikePart[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            switch (p)
            {
                case LikePart.AnySequence: sb.Append('*'); break;
                case LikePart.AnySingle s: sb.Append('?', s.Count); break;
                case LikePart.Literal l:   sb.Append(EscapeGlobLiteral(l.Value)); break;
                default: throw new NotSupportedException($"Unknown LikePart {p.GetType().Name}");
            }
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
                case LikePart.Literal l:   sb.Append(EscapeLikeLiteral(l.Value)); break;
                default: throw new NotSupportedException($"Unknown LikePart {p.GetType().Name}");
            }
        }
        return sb.ToString();
    }

    protected override string RenderExact(string value, bool ignoreCase, string column) =>
        ignoreCase
            ? $"{column} = {AddParam(value)} COLLATE NOCASE"
            : $"{column} = {AddParam(value)}";

    protected override string RenderStartsWith(string prefix, bool ignoreCase, string column) =>
        ignoreCase
            ? $"{column} LIKE {AddParam(EscapeLikeLiteral(prefix) + "%")}{LikeEscapeClause}"
            : $"{column} GLOB {AddParam(EscapeGlobLiteral(prefix) + "*")}";

    protected override string RenderEndsWith(string suffix, bool ignoreCase, string column) =>
        ignoreCase
            ? $"{column} LIKE {AddParam("%" + EscapeLikeLiteral(suffix))}{LikeEscapeClause}"
            : $"{column} GLOB {AddParam("*" + EscapeGlobLiteral(suffix))}";

    protected override string RenderContains(string value, bool ignoreCase, string column) =>
        ignoreCase
            ? $"{column} LIKE {AddParam("%" + EscapeLikeLiteral(value) + "%")}{LikeEscapeClause}"
            : $"{column} GLOB {AddParam("*" + EscapeGlobLiteral(value) + "*")}";

    protected override string RenderStartsAndEndsWith(string prefix, string suffix, bool ignoreCase, string column) =>
        ignoreCase
            ? $"{column} LIKE {AddParam(EscapeLikeLiteral(prefix) + "%" + EscapeLikeLiteral(suffix))}{LikeEscapeClause}"
            : $"{column} GLOB {AddParam(EscapeGlobLiteral(prefix) + "*" + EscapeGlobLiteral(suffix))}";

    protected override string RenderLike(LikePart[] parts, bool ignoreCase, string column) =>
        ignoreCase
            ? $"{column} LIKE {AddParam(BuildLikePattern(parts))}{LikeEscapeClause}"
            : $"{column} GLOB {AddParam(BuildGlobPattern(parts))}";

    // The (?i) inline modifier requests case-insensitive matching from any reasonable engine
    // (.NET, PCRE, ICU). The user must register a regexp function on the SQLite connection.
    protected override string RenderRegex(string pattern, bool ignoreCase, string column) =>
        $"{column} REGEXP {AddParam(ignoreCase ? "(?i)" + pattern : pattern)}";
}
