using System.Buffers;

namespace Wildcard;

internal enum SegmentKind : byte
{
    Literal,
    QuestionMark,
    Star,
    CharClass,
    QuestionRun,
}

internal readonly struct CharRange
{
    public readonly char Lo;
    public readonly char Hi;

    public CharRange(char lo, char hi)
    {
        Lo = lo;
        Hi = hi;
    }

    public CharRange(char single) : this(single, single) { }
}

internal readonly struct Segment
{
    public readonly SegmentKind Kind;
    public readonly string Literal;   // for Literal segments
    public readonly CharRange[] Ranges; // for CharClass segments
    public readonly bool Negated;      // for CharClass segments
    public readonly SearchValues<char>? SearchChars; // SIMD-accelerated lookup for CharClass
    public readonly int Count;         // for QuestionRun segments

    private Segment(SegmentKind kind, string literal, CharRange[] ranges, bool negated, SearchValues<char>? searchChars = null, int count = 0)
    {
        Kind = kind;
        Literal = literal;
        Ranges = ranges;
        Negated = negated;
        SearchChars = searchChars;
        Count = count;
    }

    public static Segment MakeLiteral(string text) =>
        new(SegmentKind.Literal, text, [], false);

    public static Segment MakeQuestion() =>
        new(SegmentKind.QuestionMark, string.Empty, [], false);

    public static Segment MakeQuestionRun(int count) =>
        new(SegmentKind.QuestionRun, string.Empty, [], false, count: count);

    public static Segment MakeStar() =>
        new(SegmentKind.Star, string.Empty, [], false);

    public static Segment MakeCharClass(CharRange[] ranges, bool negated, bool ignoreCase = false)
    {
        var searchChars = CreateSearchValues(ranges, ignoreCase);
        return new Segment(SegmentKind.CharClass, string.Empty, ranges, negated, searchChars);
    }

    private static SearchValues<char> CreateSearchValues(CharRange[] ranges, bool ignoreCase)
    {
        var chars = new List<char>();
        foreach (var range in ranges)
        {
            for (char c = range.Lo; c <= range.Hi; c++)
            {
                chars.Add(c);
                if (ignoreCase)
                {
                    var lower = char.ToLowerInvariant(c);
                    var upper = char.ToUpperInvariant(c);
                    if (lower != c) chars.Add(lower);
                    if (upper != c) chars.Add(upper);
                }
                // Guard against excessively large ranges
                if (chars.Count > 512)
                    return SearchValues.Create(chars.ToArray());
            }
        }
        return SearchValues.Create(chars.ToArray());
    }
}
