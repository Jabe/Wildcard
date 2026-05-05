using System.Text;

namespace Wildcard;

// Postgres backslash-escape with ESCAPE clause; ILIKE for case-insensitive; POSIX regex via ~ / ~*.
internal sealed class PostgresRenderer(string parameterPrefix) : SqlRenderer(parameterPrefix)
{
    private const string EscapeClause = @" ESCAPE '\'";

    private static string EscapeLiteral(string value)
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

    protected override string RenderExact(string value, bool ignoreCase, string column) =>
        ignoreCase
            ? $"LOWER({column}) = LOWER({AddParam(value)})"
            : $"{column} = {AddParam(value)}";

    protected override string RenderStartsWith(string prefix, bool ignoreCase, string column) =>
        RenderLikeBody(EscapeLiteral(prefix) + "%", ignoreCase, column);

    protected override string RenderEndsWith(string suffix, bool ignoreCase, string column) =>
        RenderLikeBody("%" + EscapeLiteral(suffix), ignoreCase, column);

    protected override string RenderContains(string value, bool ignoreCase, string column) =>
        RenderLikeBody("%" + EscapeLiteral(value) + "%", ignoreCase, column);

    protected override string RenderStartsAndEndsWith(string prefix, string suffix, bool ignoreCase, string column) =>
        RenderLikeBody(EscapeLiteral(prefix) + "%" + EscapeLiteral(suffix), ignoreCase, column);

    protected override string RenderLike(LikePart[] parts, bool ignoreCase, string column) =>
        RenderLikeBody(BuildLikePattern(parts), ignoreCase, column);

    protected override string RenderRegex(string pattern, bool ignoreCase, string column) =>
        $"{column} {(ignoreCase ? "~*" : "~")} {AddParam(pattern)}";

    private string RenderLikeBody(string likePattern, bool ignoreCase, string column) =>
        $"{column} {(ignoreCase ? "ILIKE" : "LIKE")} {AddParam(likePattern)}{EscapeClause}";
}
