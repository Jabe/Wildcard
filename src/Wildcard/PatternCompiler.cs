using System.Buffers;
using System.Text;

namespace Wildcard;

internal static class PatternCompiler
{
    public static Segment[] Compile(string pattern, bool ignoreCase = false)
    {
        // Rent a pooled array — max segments can't exceed pattern length
        var segments = ArrayPool<Segment>.Shared.Rent(pattern.Length);
        int segCount = 0;
        var literalBuf = new StringBuilder();
        int i = 0;

        while (i < pattern.Length)
        {
            char c = pattern[i];

            switch (c)
            {
                case '*':
                    FlushLiteral(segments, ref segCount, literalBuf);
                    // Collapse consecutive stars
                    if (segCount == 0 || segments[segCount - 1].Kind != SegmentKind.Star)
                        segments[segCount++] = Segment.MakeStar();
                    i++;
                    break;

                case '?':
                    FlushLiteral(segments, ref segCount, literalBuf);
                    int qCount = 1;
                    while (i + 1 < pattern.Length && pattern[i + 1] == '?')
                    {
                        qCount++;
                        i++;
                    }
                    segments[segCount++] = qCount == 1 ? Segment.MakeQuestion() : Segment.MakeQuestionRun(qCount);
                    i++;
                    break;

                case '[':
                    i++;
                    var ccSeg = ParseCharClass(pattern, ref i, ignoreCase);
                    // Promote single-char non-negated class to literal (merges with adjacent)
                    if (!ccSeg.Negated && ccSeg.Ranges.Length == 1 && ccSeg.Ranges[0].Lo == ccSeg.Ranges[0].Hi)
                    {
                        literalBuf.Append(ccSeg.Ranges[0].Lo);
                    }
                    else
                    {
                        FlushLiteral(segments, ref segCount, literalBuf);
                        segments[segCount++] = ccSeg;
                    }
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

        FlushLiteral(segments, ref segCount, literalBuf);

        var result = segCount == 0 ? [] : segments.AsSpan(0, segCount).ToArray();
        ArrayPool<Segment>.Shared.Return(segments);
        return result;
    }

    private static void FlushLiteral(Segment[] segments, ref int count, StringBuilder buf)
    {
        if (buf.Length > 0)
        {
            segments[count++] = Segment.MakeLiteral(buf.ToString());
            buf.Clear();
        }
    }

    private static Segment ParseCharClass(string pattern, ref int i, bool ignoreCase)
    {
        bool negated = false;
        // Rent a pooled array — max ranges can't exceed remaining pattern length
        var ranges = ArrayPool<CharRange>.Shared.Rent(pattern.Length - i);
        int rangeCount = 0;

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
                ranges[rangeCount++] = new CharRange(lo, hi);
                i += 3;
            }
            else
            {
                ranges[rangeCount++] = new CharRange(c);
                i++;
            }
        }

        var result = Segment.MakeCharClass(
            rangeCount == 0 ? [] : ranges.AsSpan(0, rangeCount).ToArray(),
            negated, ignoreCase);
        ArrayPool<CharRange>.Shared.Return(ranges);
        return result;
    }
}
