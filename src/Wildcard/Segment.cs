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

    private Segment(SegmentKind kind, string literal, CharRange[] ranges, bool negated)
    {
        Kind = kind;
        Literal = literal;
        Ranges = ranges;
        Negated = negated;
    }

    public static Segment MakeLiteral(string text) =>
        new(SegmentKind.Literal, text, Array.Empty<CharRange>(), false);

    public static Segment MakeQuestion() =>
        new(SegmentKind.QuestionMark, string.Empty, Array.Empty<CharRange>(), false);

    public static Segment MakeStar() =>
        new(SegmentKind.Star, string.Empty, Array.Empty<CharRange>(), false);

    public static Segment MakeCharClass(CharRange[] ranges, bool negated) =>
        new(SegmentKind.CharClass, string.Empty, ranges, negated);
}
