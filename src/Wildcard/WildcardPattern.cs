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

    internal enum PatternShape : byte
    {
        General,
        PureLiteral,
        StarSuffix,
        PrefixStar,
        PrefixStarSuffix,
        StarContainsStar,
    }

    /// <summary>
    /// The minimum input length that could possibly match this pattern.
    /// </summary>
    public int MinLength { get; }

    internal PatternShape Shape => _shape;
    internal string? Prefix => _prefix;
    internal string? Suffix => _suffix;
    internal bool IgnoreCase => _ignoreCase;

    private WildcardPattern(string pattern, Segment[] segments, bool ignoreCase)
    {
        _original = pattern;
        _segments = segments;
        _ignoreCase = ignoreCase;
        _shape = DetectShape(segments, out _prefix, out _suffix);
        MinLength = ComputeMinLength(segments);
    }

    private static int ComputeMinLength(Segment[] segments)
    {
        int len = 0;
        foreach (ref readonly var seg in segments.AsSpan())
        {
            len += seg.Kind switch
            {
                SegmentKind.Literal => seg.Literal.Length,
                SegmentKind.QuestionMark => 1,
                SegmentKind.QuestionRun => seg.Count,
                SegmentKind.CharClass => 1,
                SegmentKind.Star => 0,
                _ => 0,
            };
        }
        return len;
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

    /// <summary>
    /// Returns the original pattern string used to compile this <see cref="WildcardPattern"/>.
    /// </summary>
    public override string ToString() => _original;

    /// <summary>
    /// Matches <paramref name="input"/> against this pattern and extracts the substrings matched by each * segment.
    /// Returns true if the input matches. On match, <paramref name="captures"/> contains one entry per * in the pattern.
    /// On failure, <paramref name="captures"/> is empty.
    /// </summary>
    public bool TryMatch(string input, out string[] captures)
    {
        ArgumentNullException.ThrowIfNull(input);
        return TryMatch(input.AsSpan(), out captures);
    }

    /// <summary>
    /// Matches <paramref name="input"/> against this pattern and extracts the substrings matched by each * segment.
    /// </summary>
    public bool TryMatch(ReadOnlySpan<char> input, out string[] captures)
    {
        switch (_shape)
        {
            case PatternShape.PureLiteral:
                if (_ignoreCase
                    ? input.Equals(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.SequenceEqual(_prefix))
                {
                    captures = [];
                    return true;
                }
                captures = [];
                return false;

            case PatternShape.StarSuffix:
                if (_ignoreCase
                    ? input.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
                    : input.EndsWith(_suffix))
                {
                    captures = [input[..^_suffix!.Length].ToString()];
                    return true;
                }
                captures = [];
                return false;

            case PatternShape.PrefixStar:
                if (_ignoreCase
                    ? input.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.StartsWith(_prefix))
                {
                    captures = [input[_prefix!.Length..].ToString()];
                    return true;
                }
                captures = [];
                return false;

            case PatternShape.PrefixStarSuffix:
                if (input.Length < _prefix!.Length + _suffix!.Length)
                {
                    captures = [];
                    return false;
                }
                var psStartOk = _ignoreCase
                    ? input.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.StartsWith(_prefix);
                if (!psStartOk)
                {
                    captures = [];
                    return false;
                }
                var psEndOk = _ignoreCase
                    ? input.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
                    : input.EndsWith(_suffix);
                if (!psEndOk)
                {
                    captures = [];
                    return false;
                }
                captures = [input[_prefix.Length..^_suffix.Length].ToString()];
                return true;

            case PatternShape.StarContainsStar:
                var idx = _ignoreCase
                    ? input.IndexOf(_prefix, StringComparison.OrdinalIgnoreCase)
                    : input.IndexOf(_prefix);
                if (idx < 0)
                {
                    captures = [];
                    return false;
                }
                captures = [
                    input[..idx].ToString(),
                    input[(idx + _prefix!.Length)..].ToString()
                ];
                return true;

            default:
                return MatchCoreWithCaptures(_segments.AsSpan(), input, _ignoreCase, out captures);
        }
    }

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

    /// <summary>
    /// Converts this wildcard pattern into a <see cref="PatternPredicate"/> that describes
    /// the match operation structurally (e.g. starts-with, contains, exact).
    /// Useful for translating patterns to SQL LIKE clauses or other query languages.
    /// Complex patterns that cannot be simplified are returned as <see cref="PatternPredicate.Regex"/>.
    /// </summary>
    public PatternPredicate ToPredicate()
    {
        if (_segments.Length == 0)
            return new PatternPredicate.Exact("", _ignoreCase);

        return _shape switch
        {
            PatternShape.PureLiteral      => new PatternPredicate.Exact(_prefix!, _ignoreCase),
            PatternShape.PrefixStar       => new PatternPredicate.StartsWith(_prefix!, _ignoreCase),
            PatternShape.StarSuffix       => new PatternPredicate.EndsWith(_suffix!, _ignoreCase),
            PatternShape.StarContainsStar => new PatternPredicate.Contains(_prefix!, _ignoreCase),
            PatternShape.PrefixStarSuffix => new PatternPredicate.StartsAndEndsWith(_prefix!, _suffix!, _ignoreCase),
            _                             => new PatternPredicate.Regex(ToRegex().ToString(), _ignoreCase),
        };
    }

    /// <summary>
    /// Compiles a pattern and returns its <see cref="PatternPredicate"/> in one step.
    /// </summary>
    public static PatternPredicate ToPredicate(string pattern, bool ignoreCase = false)
        => Compile(pattern, ignoreCase).ToPredicate();

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
        => MatchCore(segments, input, ignoreCase, null, null, null);

    private static bool MatchCore(
        ReadOnlySpan<Segment> segments, ReadOnlySpan<char> input, bool ignoreCase,
        int[]? starStarts, int[]? starEnds, int[]? segToStar)
    {
        bool capturing = starStarts is not null;
        int segIdx = 0, inputIdx = 0;
        int starSegIdx = -1, starInputIdx = -1;
        int activeStarIdx = -1;

        while (inputIdx < input.Length)
        {
            if (segIdx < segments.Length)
            {
                ref readonly var seg = ref segments[segIdx];

                switch (seg.Kind)
                {
                    case SegmentKind.Literal:
                        int beforeLit = inputIdx;
                        if (MatchLiteral(seg.Literal.AsSpan(), input, ref inputIdx, ignoreCase))
                        {
                            if (capturing && activeStarIdx >= 0)
                            {
                                starEnds![activeStarIdx] = beforeLit;
                                activeStarIdx = -1;
                            }
                            segIdx++;
                            continue;
                        }
                        break;

                    case SegmentKind.QuestionMark:
                        if (capturing && activeStarIdx >= 0)
                        {
                            starEnds![activeStarIdx] = inputIdx;
                            activeStarIdx = -1;
                        }
                        inputIdx++;
                        segIdx++;
                        continue;

                    case SegmentKind.QuestionRun:
                        if (input.Length - inputIdx >= seg.Count)
                        {
                            if (capturing && activeStarIdx >= 0)
                            {
                                starEnds![activeStarIdx] = inputIdx;
                                activeStarIdx = -1;
                            }
                            inputIdx += seg.Count;
                            segIdx++;
                            continue;
                        }
                        break;

                    case SegmentKind.Star:
                        if (capturing)
                        {
                            if (activeStarIdx >= 0)
                                starEnds![activeStarIdx] = inputIdx;
                            activeStarIdx = segToStar![segIdx];
                            starStarts![activeStarIdx] = inputIdx;
                        }
                        starSegIdx = segIdx;
                        starInputIdx = inputIdx;
                        segIdx++;
                        // If this star is the last segment, it matches everything remaining
                        if (segIdx >= segments.Length)
                        {
                            if (capturing)
                                starEnds![activeStarIdx] = input.Length;
                            return true;
                        }
                        if (segments[segIdx].Kind == SegmentKind.Literal)
                        {
                            var nextLit = segments[segIdx].Literal.AsSpan();
                            // EndsWith fast-path: if this literal is the last meaningful segment
                            if (IsLastSegment(segments, segIdx))
                            {
                                int suffixStart = input.Length - nextLit.Length;
                                if (suffixStart < inputIdx) return false;
                                bool endMatch = ignoreCase
                                    ? input[suffixStart..].Equals(nextLit, StringComparison.OrdinalIgnoreCase)
                                    : input[suffixStart..].SequenceEqual(nextLit);
                                if (endMatch && capturing)
                                    starEnds![activeStarIdx] = suffixStart;
                                return endMatch;
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
                            if (capturing && activeStarIdx >= 0)
                            {
                                starEnds![activeStarIdx] = inputIdx;
                                activeStarIdx = -1;
                            }
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
                if (capturing)
                    activeStarIdx = segToStar![starSegIdx];
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

        // Close active star if it's at the end
        if (capturing && activeStarIdx >= 0)
            starEnds![activeStarIdx] = inputIdx;

        // Consume trailing stars
        while (segIdx < segments.Length && segments[segIdx].Kind == SegmentKind.Star)
        {
            if (capturing)
            {
                int idx = segToStar![segIdx];
                starStarts![idx] = inputIdx;
                starEnds![idx] = inputIdx;
            }
            segIdx++;
        }

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
        // SearchChars is always non-null for CharClass segments (CreateSearchValues never returns null).
        // SIMD-accelerated path; for case-insensitive patterns, SearchValues already contains both cases.
        bool found = seg.SearchChars!.Contains(c);
        return seg.Negated ? !found : found;
    }

    private static bool MatchCoreWithCaptures(ReadOnlySpan<Segment> segments, ReadOnlySpan<char> input, bool ignoreCase, out string[] captures)
    {
        // Count stars to size the captures array
        int starCount = 0;
        foreach (ref readonly var s in segments)
        {
            if (s.Kind == SegmentKind.Star) starCount++;
        }

        if (starCount == 0)
        {
            captures = [];
            return MatchCore(segments, input, ignoreCase);
        }

        var starStarts = new int[starCount];
        var starEnds = new int[starCount];

        // Map segment index → star index
        var segToStar = new int[segments.Length];
        int si = 0;
        for (int j = 0; j < segments.Length; j++)
            segToStar[j] = segments[j].Kind == SegmentKind.Star ? si++ : -1;

        if (!MatchCore(segments, input, ignoreCase, starStarts, starEnds, segToStar))
        {
            captures = [];
            return false;
        }

        captures = new string[starCount];
        for (int j = 0; j < starCount; j++)
            captures[j] = input[starStarts[j]..starEnds[j]].ToString();
        return true;
    }
}
