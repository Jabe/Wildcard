namespace Wildcard;

/// <summary>
/// Expands brace patterns like <c>{a,b,c}</c> into multiple strings.
/// Handles nesting, escaping, and multiple brace groups (cartesian product).
/// </summary>
internal static class BraceExpander
{
    /// <summary>
    /// Maximum number of expanded results to prevent combinatorial explosion.
    /// </summary>
    private const int MaxExpansions = 1024;

    /// <summary>
    /// Expands all brace groups in the pattern. Returns a single-element array if no braces are found.
    /// </summary>
    public static string[] Expand(string pattern)
    {
        var results = ExpandRecursive(pattern);
        return results.Count == 0 ? [pattern] : [.. results];
    }

    private static List<string> ExpandRecursive(string pattern)
    {
        // Find the first top-level brace group (respecting escapes and char classes)
        int braceStart = FindBraceOpen(pattern);
        if (braceStart < 0)
            return [pattern];

        int braceEnd = FindBraceClose(pattern, braceStart);
        if (braceEnd < 0)
        {
            // Unmatched '{' — treat as literal
            return [pattern];
        }

        var prefix = pattern[..braceStart];
        var suffix = pattern[(braceEnd + 1)..];
        var alternatives = SplitAlternatives(pattern, braceStart + 1, braceEnd);

        var results = new List<string>();
        foreach (var alt in alternatives)
        {
            // Recurse to expand remaining brace groups in (prefix + alt + suffix)
            var expanded = ExpandRecursive(prefix + alt + suffix);
            foreach (var e in expanded)
            {
                results.Add(e);
                if (results.Count >= MaxExpansions)
                    return results;
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the index of the first unescaped '{' that is not inside a character class.
    /// Returns -1 if none found.
    /// </summary>
    private static int FindBraceOpen(string pattern)
    {
        bool inCharClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];

            if (c == '\\' && i + 1 < pattern.Length)
            {
                i++; // skip escaped char
                continue;
            }

            if (c == '[')
            {
                inCharClass = true;
                continue;
            }

            if (c == ']' && inCharClass)
            {
                inCharClass = false;
                continue;
            }

            if (c == '{' && !inCharClass)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Finds the matching '}' for a '{' at the given position, handling nesting.
    /// Returns -1 if no matching brace is found.
    /// </summary>
    private static int FindBraceClose(string pattern, int openPos)
    {
        int depth = 1;
        bool inCharClass = false;

        for (int i = openPos + 1; i < pattern.Length; i++)
        {
            char c = pattern[i];

            if (c == '\\' && i + 1 < pattern.Length)
            {
                i++; // skip escaped char
                continue;
            }

            if (c == '[')
            {
                inCharClass = true;
                continue;
            }

            if (c == ']' && inCharClass)
            {
                inCharClass = false;
                continue;
            }

            if (inCharClass)
                continue;

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits the content between braces on ',' at the top level (respecting nested braces).
    /// </summary>
    private static List<string> SplitAlternatives(string pattern, int start, int end)
    {
        var alternatives = new List<string>();
        int depth = 0;
        bool inCharClass = false;
        int segStart = start;

        for (int i = start; i < end; i++)
        {
            char c = pattern[i];

            if (c == '\\' && i + 1 < end)
            {
                i++; // skip escaped char
                continue;
            }

            if (c == '[')
            {
                inCharClass = true;
                continue;
            }

            if (c == ']' && inCharClass)
            {
                inCharClass = false;
                continue;
            }

            if (inCharClass)
                continue;

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                alternatives.Add(pattern[segStart..i]);
                segStart = i + 1;
            }
        }

        alternatives.Add(pattern[segStart..end]);
        return alternatives;
    }
}
