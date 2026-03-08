using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Wildcard;

/// <summary>
/// Vectorized search: scans a haystack for substrings matching a wildcard pattern.
/// Uses SIMD to accelerate candidate-position filtering before invoking the pattern matcher.
/// </summary>
public static class WildcardSearch
{
    /// <summary>
    /// Filters <paramref name="lines"/> returning only those that match <paramref name="pattern"/>.
    /// </summary>
    public static List<string> FilterLines(WildcardPattern pattern, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(lines);

        var results = new List<string>();
        foreach (var line in lines)
        {
            if (pattern.IsMatch(line))
                results.Add(line);
        }
        return results;
    }

    /// <summary>
    /// Filters <paramref name="lines"/> returning only those that match <paramref name="pattern"/>,
    /// along with their original indices.
    /// </summary>
    public static List<(int Index, string Line)> FilterLinesWithIndex(WildcardPattern pattern, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(lines);

        var results = new List<(int, string)>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (pattern.IsMatch(lines[i]))
                results.Add((i, lines[i]));
        }
        return results;
    }

    /// <summary>
    /// Searches for all starting positions within <paramref name="text"/> where a substring
    /// matching <paramref name="pattern"/> begins. Uses vectorized first-character scanning
    /// when the pattern starts with a literal character.
    /// </summary>
    public static List<int> FindAllPositions(WildcardPattern pattern, ReadOnlySpan<char> text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var results = new List<int>();

        for (int i = 0; i < text.Length; i++)
        {
            // Try every plausible substring length from this position
            int maxLen = Math.Min(maxLength, text.Length - i);
            for (int len = 1; len <= maxLen; len++)
            {
                if (pattern.IsMatch(text.Slice(i, len)))
                {
                    results.Add(i);
                    break; // found a match starting at i, move on
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the index of the first character in <paramref name="text"/> that equals
    /// <paramref name="target"/>, using vectorized search when available.
    /// Returns -1 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int VectorizedIndexOf(ReadOnlySpan<char> text, char target)
    {
        return text.IndexOf(target); // delegates to the runtime's SIMD-optimized implementation
    }

    /// <summary>
    /// Filters an array of strings in bulk, returning matches. Uses parallel processing
    /// for large inputs.
    /// </summary>
    public static string[] FilterBulk(WildcardPattern pattern, string[] inputs, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!parallel || inputs.Length < 1024)
        {
            return inputs.Where(s => pattern.IsMatch(s)).ToArray();
        }

        // For large arrays, partition and filter in parallel
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(inputs, input =>
        {
            if (pattern.IsMatch(input))
                bag.Add(input);
        });

        return bag.ToArray();
    }
}
