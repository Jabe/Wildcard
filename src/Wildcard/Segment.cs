using System.Buffers;

namespace Wildcard;

internal enum SegmentKind : byte
{
    Literal,
    QuestionMark,
    Star,
    CharClass,
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

    private Segment(SegmentKind kind, string literal, CharRange[] ranges, bool negated, SearchValues<char>? searchChars = null)
    {
        Kind = kind;
        Literal = literal;
        Ranges = ranges;
        Negated = negated;
        SearchChars = searchChars;
    }

    public static Segment MakeLiteral(string text) =>
        new(SegmentKind.Literal, text, [], false);

    public static Segment MakeQuestion() =>
        new(SegmentKind.QuestionMark, string.Empty, [], false);

    public static Segment MakeStar() =>
        new(SegmentKind.Star, string.Empty, [], false);

    public static Segment MakeCharClass(CharRange[] ranges, bool negated)
    {
        var searchChars = CreateSearchValues(ranges);
        return new Segment(SegmentKind.CharClass, string.Empty, ranges, negated, searchChars);
    }

    private static SearchValues<char> CreateSearchValues(CharRange[] ranges)
    {
        var chars = new List<char>();
        foreach (var range in ranges)
        {
            for (char c = range.Lo; c <= range.Hi; c++)
            {
                chars.Add(c);
                // Guard against excessively large ranges
                if (chars.Count > 256)
                    return SearchValues.Create(chars.ToArray());
            }
        }
        return SearchValues.Create(chars.ToArray());
    }
}
