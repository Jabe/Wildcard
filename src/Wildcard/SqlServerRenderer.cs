using System.Text;

namespace Wildcard;

// T-SQL bracket-escape: [%] [_] [[]. No native regex.
internal sealed class SqlServerRenderer(string parameterPrefix) : SqlRenderer(parameterPrefix)
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

    protected override string RenderExact(string value, bool ignoreCase, string column) =>
        ignoreCase
            ? $"LOWER({column}) = {AddParam(value.ToLowerInvariant())}"
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

    // Uses REGEXP_LIKE introduced in SQL Server 2025 / Azure SQL Database / Managed Instance / Fabric SQL.
    // Requires database compatibility level 170+.
    protected override string RenderRegex(string pattern, bool ignoreCase, string column) =>
        ignoreCase
            ? $"REGEXP_LIKE({column}, {AddParam(pattern)}, 'i')"
            : $"REGEXP_LIKE({column}, {AddParam(pattern)})";

    private string RenderLikeBody(string likePattern, bool ignoreCase, string column) =>
        ignoreCase
            ? $"LOWER({column}) LIKE {AddParam(likePattern.ToLowerInvariant())}"
            : $"{column} LIKE {AddParam(likePattern)}";
}
