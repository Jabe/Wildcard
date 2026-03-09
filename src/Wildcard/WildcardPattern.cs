using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Wildcard;

/// <summary>
/// A compiled wildcard pattern that matches strings using *, ?, and character classes [abc], [a-z], [!x].
/// Designed to avoid the full Regex engine overhead.
/// </summary>
public sealed class WildcardPattern
{
    private readonly Segment[] _segments;
    private readonly string _original;
    private readonly bool _ignoreCase;
    private readonly PatternShape _shape;
    private readonly string? _prefix;
    private readonly string? _suffix;

    private enum PatternShape : byte
    {
        General,
        PureLiteral,
        StarSuffix,
        PrefixStar,
        PrefixStarSuffix,
        StarContainsStar,
    }

    private WildcardPattern(string pattern, Segment[] segments, bool ignoreCase)
    {
        _original = pattern;
        _segments = segments;
        _ignoreCase = ignoreCase;
        _shape = DetectShape(segments, out _prefix, out _suffix);
    }

    private static PatternShape DetectShape(Segment[] segments, out string? prefix, out string? suffix)
    {
        prefix = null;
        suffix = null;

        switch (segments.Length)
        {
            case 1 when segments[0].Kind == SegmentKind.Literal:
                prefix = segments[0].Literal;
                return PatternShape.PureLiteral;

            case 2 when segments[0].Kind == SegmentKind.Star && segments[1].Kind == SegmentKind.Literal:
                suffix = segments[1].Literal;
                return PatternShape.StarSuffix;

            case 2 when segments[0].Kind == SegmentKind.Literal && segments[1].Kind == SegmentKind.Star:
                prefix = segments[0].Literal;
                return PatternShape.PrefixStar;

            case 3 when segments[0].Kind == SegmentKind.Literal && segments[1].Kind == SegmentKind.Star && segments[2].Kind == SegmentKind.Literal:
                prefix = segments[0].Literal;
                suffix = segments[2].Literal;
                return PatternShape.PrefixStarSuffix;

            case 3 when segments[0].Kind == SegmentKind.Star && segments[1].Kind == SegmentKind.Literal && segments[2].Kind == SegmentKind.Star:
                prefix = segments[1].Literal;
                return PatternShape.StarContainsStar;

            default:
                return PatternShape.General;
        }
    }

    /// <summary>
    /// Compiles a wildcard pattern string into a <see cref="WildcardPattern"/>.
    /// Supported syntax: * (any sequence), ? (any single char), [abc] (char class), [a-z] (range), [!x] (negation).
    /// </summary>
    public static WildcardPattern Compile(string pattern, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var segments = PatternCompiler.Compile(pattern, ignoreCase);
        return new WildcardPattern(pattern, segments, ignoreCase);
    }

    /// <summary>
    /// Returns true if <paramref name="input"/> matches this pattern.
    /// </summary>
    public bool IsMatch(ReadOnlySpan<char> input)
    {
        switch (_shape)
        {
            case PatternShape.PureLiteral:
                return _ignoreCase
                    ? input.Equals(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.SequenceEqual(_prefix);

            case PatternShape.StarSuffix:
                return _ignoreCase
                    ? input.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
                    : input.EndsWith(_suffix);

            case PatternShape.PrefixStar:
                return _ignoreCase
                    ? input.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.StartsWith(_prefix);

            case PatternShape.PrefixStarSuffix:
                if (input.Length < _prefix!.Length + _suffix!.Length) return false;
                var startOk = _ignoreCase
                    ? input.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.StartsWith(_prefix);
                if (!startOk) return false;
                return _ignoreCase
                    ? input.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
                    : input.EndsWith(_suffix);

            case PatternShape.StarContainsStar:
                return _ignoreCase
                    ? input.IndexOf(_prefix, StringComparison.OrdinalIgnoreCase) >= 0
                    : input.IndexOf(_prefix) >= 0;

            default:
                return MatchCore(_segments.AsSpan(), input, _ignoreCase);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="input"/> matches this pattern.
    /// </summary>
    public bool IsMatch(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsMatch(input.AsSpan());
    }

    /// <summary>
    /// One-shot match without pre-compiling a pattern.
    /// </summary>
    public static bool IsMatch(string pattern, string input, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(input);
        var compiled = Compile(pattern, ignoreCase);
        return compiled.IsMatch(input);
    }

    public override string ToString() => _original;

    /// <summary>
    /// Converts this wildcard pattern into an equivalent <see cref="Regex"/>.
    /// Useful for interop with APIs that accept <see cref="Regex"/>.
    /// </summary>
    public Regex ToRegex()
    {
        var sb = new StringBuilder("^");
        int i = 0;

        while (i < _original.Length)
        {
            char c = _original[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    // Collapse consecutive stars
                    while (i + 1 < _original.Length && _original[i + 1] == '*') i++;
                    i++;
                    break;

                case '?':
                    sb.Append('.');
                    i++;
                    break;

                case '[':
                    sb.Append('[');
                    i++;
                    // Handle negation: [! or [^ → [^
                    if (i < _original.Length && (_original[i] == '!' || _original[i] == '^'))
                    {
                        sb.Append('^');
                        i++;
                    }
                    // Copy until closing ]
                    bool first = true;
                    while (i < _original.Length)
                    {
                        char cc = _original[i];
                        if (cc == ']' && !first)
                        {
                            sb.Append(']');
                            i++;
                            break;
                        }
                        first = false;
                        if (cc == '\\' && i + 1 < _original.Length)
                        {
                            sb.Append('\\');
                            i++;
                            sb.Append(_original[i]);
                            i++;
                        }
                        else
                        {
                            sb.Append(cc);
                            i++;
                        }
                    }
                    break;

                case '\\':
                    i++;
                    if (i < _original.Length)
                    {
                        sb.Append(Regex.Escape(_original[i].ToString()));
                        i++;
                    }
                    break;

                default:
                    // Escape regex metacharacters in literals
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        sb.Append('$');
        var options = _ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return new Regex(sb.ToString(), options);
    }

    // --- Core matching engine using backtracking with optimized fast-paths ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CharsEqual(char a, char b, bool ignoreCase)
    {
        if (a == b) return true;
        if (!ignoreCase) return false;
        return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLastSegment(ReadOnlySpan<Segment> segments, int idx)
        => idx + 1 >= segments.Length;

    private static bool MatchCore(ReadOnlySpan<Segment> segments, ReadOnlySpan<char> input, bool ignoreCase)
    {
        // dp-style backtracking: track the last '*' position
        int segIdx = 0, inputIdx = 0;
        int starSegIdx = -1, starInputIdx = -1;

        while (inputIdx < input.Length)
        {
            if (segIdx < segments.Length)
            {
                ref readonly var seg = ref segments[segIdx];

                switch (seg.Kind)
                {
                    case SegmentKind.Literal:
                        if (MatchLiteral(seg.Literal.AsSpan(), input, ref inputIdx, ignoreCase))
                        {
                            segIdx++;
                            continue;
                        }
                        break;

                    case SegmentKind.QuestionMark:
                        inputIdx++;
                        segIdx++;
                        continue;

                    case SegmentKind.QuestionRun:
                        if (input.Length - inputIdx >= seg.Count)
                        {
                            inputIdx += seg.Count;
                            segIdx++;
                            continue;
                        }
                        break;

                    case SegmentKind.Star:
                        starSegIdx = segIdx;
                        starInputIdx = inputIdx;
                        segIdx++;
                        // If this star is the last segment, it matches everything remaining
                        if (segIdx >= segments.Length)
                            return true;
                        if (segments[segIdx].Kind == SegmentKind.Literal)
                        {
                            var nextLit = segments[segIdx].Literal.AsSpan();
                            // EndsWith fast-path: if this literal is the last meaningful segment
                            if (IsLastSegment(segments, segIdx))
                            {
                                int suffixStart = input.Length - nextLit.Length;
                                if (suffixStart < inputIdx) return false;
                                return ignoreCase
                                    ? input[suffixStart..].Equals(nextLit, StringComparison.OrdinalIgnoreCase)
                                    : input[suffixStart..].SequenceEqual(nextLit);
                            }
                            // IndexOf fast-path: jump to first occurrence of the next literal
                            int found = ignoreCase
                                ? input[inputIdx..].IndexOf(nextLit, StringComparison.OrdinalIgnoreCase)
                                : input[inputIdx..].IndexOf(nextLit);
                            if (found < 0) return false;
                            inputIdx += found;
                            starInputIdx = inputIdx;
                        }
                        continue;

                    case SegmentKind.CharClass:
                        if (MatchCharClass(seg, input[inputIdx], ignoreCase))
                        {
                            inputIdx++;
                            segIdx++;
                            continue;
                        }
                        break;
                }
            }

            // Backtrack to last star
            if (starSegIdx >= 0)
            {
                segIdx = starSegIdx + 1;
                if (segIdx < segments.Length && segments[segIdx].Kind == SegmentKind.Literal)
                {
                    var nextLit = segments[segIdx].Literal.AsSpan();
                    int searchFrom = starInputIdx + 1;
                    if (searchFrom >= input.Length) return false;
                    int found = ignoreCase
                        ? input[searchFrom..].IndexOf(nextLit, StringComparison.OrdinalIgnoreCase)
                        : input[searchFrom..].IndexOf(nextLit);
                    if (found < 0) return false;
                    starInputIdx = searchFrom + found;
                    inputIdx = starInputIdx;
                }
                else
                {
                    starInputIdx++;
                    inputIdx = starInputIdx;
                }
                continue;
            }

            return false;
        }

        // Consume trailing stars
        while (segIdx < segments.Length && segments[segIdx].Kind == SegmentKind.Star)
            segIdx++;

        return segIdx == segments.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchLiteral(ReadOnlySpan<char> literal, ReadOnlySpan<char> input, ref int inputIdx, bool ignoreCase)
    {
        if (literal.Length == 1)
        {
            if (inputIdx < input.Length && CharsEqual(literal[0], input[inputIdx], ignoreCase))
            {
                inputIdx++;
                return true;
            }
            return false;
        }

        int remaining = input.Length - inputIdx;
        if (remaining < literal.Length)
            return false;

        var slice = input.Slice(inputIdx, literal.Length);
        if (ignoreCase)
        {
            if (!slice.Equals(literal, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else
        {
            if (!slice.SequenceEqual(literal))
                return false;
        }

        inputIdx += literal.Length;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchCharClass(in Segment seg, char c, bool ignoreCase)
    {
        bool found;

        if (seg.SearchChars is not null)
        {
            // SIMD-accelerated path using SearchValues<char>
            // For case-insensitive patterns, SearchValues already contains both cases
            found = seg.SearchChars.Contains(c);
        }
        else
        {
            found = false;
            foreach (var range in seg.Ranges.AsSpan())
            {
                if (ignoreCase)
                {
                    char cu = char.ToUpperInvariant(c);
                    if (cu >= char.ToUpperInvariant(range.Lo) && cu <= char.ToUpperInvariant(range.Hi))
                    {
                        found = true;
                        break;
                    }
                }
                else
                {
                    if (c >= range.Lo && c <= range.Hi)
                    {
                        found = true;
                        break;
                    }
                }
            }
        }

        return seg.Negated ? !found : found;
    }
}
