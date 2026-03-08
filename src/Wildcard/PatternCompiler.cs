using System.Text;

namespace Wildcard;

internal static class PatternCompiler
{
    public static Segment[] Compile(string pattern)
    {
        var segments = new List<Segment>();
        var literalBuf = new StringBuilder();
        int i = 0;

        while (i < pattern.Length)
        {
            char c = pattern[i];

            switch (c)
            {
                case '*':
                    FlushLiteral(segments, literalBuf);
                    // Collapse consecutive stars
                    if (segments.Count == 0 || segments[^1].Kind != SegmentKind.Star)
                        segments.Add(Segment.MakeStar());
                    i++;
                    break;

                case '?':
                    FlushLiteral(segments, literalBuf);
                    segments.Add(Segment.MakeQuestion());
                    i++;
                    break;

                case '[':
                    FlushLiteral(segments, literalBuf);
                    i++;
                    segments.Add(ParseCharClass(pattern, ref i));
                    break;

                case '\\':
                    // Escape next character
                    i++;
                    if (i < pattern.Length)
                    {
                        literalBuf.Append(pattern[i]);
                        i++;
                    }
                    break;

                default:
                    literalBuf.Append(c);
                    i++;
                    break;
            }
        }

        FlushLiteral(segments, literalBuf);
        return segments.ToArray();
    }

    private static void FlushLiteral(List<Segment> segments, StringBuilder buf)
    {
        if (buf.Length > 0)
        {
            segments.Add(Segment.MakeLiteral(buf.ToString()));
            buf.Clear();
        }
    }

    private static Segment ParseCharClass(string pattern, ref int i)
    {
        bool negated = false;
        var ranges = new List<CharRange>();

        if (i < pattern.Length && (pattern[i] == '!' || pattern[i] == '^'))
        {
            negated = true;
            i++;
        }

        // A ']' as the very first character in the class is treated as a literal ']'
        bool first = true;

        while (i < pattern.Length)
        {
            char c = pattern[i];

            if (c == ']' && !first)
            {
                i++; // consume ']'
                break;
            }

            first = false;

            if (c == '\\' && i + 1 < pattern.Length)
            {
                i++;
                c = pattern[i];
            }

            // Check for range: a-z
            if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
            {
                char lo = c;
                char hi = pattern[i + 2];
                if (hi == '\\' && i + 3 < pattern.Length)
                {
                    hi = pattern[i + 3];
                    i++;
                }
                ranges.Add(new CharRange(lo, hi));
                i += 3;
            }
            else
            {
                ranges.Add(new CharRange(c));
                i++;
            }
        }

        return Segment.MakeCharClass(ranges.ToArray(), negated);
    }
}
