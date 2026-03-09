namespace Wildcard;

/// <summary>
/// File system globber with support for <c>*</c>, <c>?</c>, <c>[abc]</c>, and <c>**</c> (recursive directory matching).
/// </summary>
public sealed class Glob
{
    private readonly GlobSegment[] _segments;

    private enum GlobSegmentKind : byte { Literal, Pattern, DoubleStar }

    private readonly struct GlobSegment
    {
        public readonly GlobSegmentKind Kind;
        public readonly string? LiteralName;
        public readonly WildcardPattern? Pattern;

        private GlobSegment(GlobSegmentKind kind, string? literal, WildcardPattern? pattern)
        {
            Kind = kind;
            LiteralName = literal;
            Pattern = pattern;
        }

        public static GlobSegment MakeLiteral(string name) => new(GlobSegmentKind.Literal, name, null);
        public static GlobSegment MakePattern(WildcardPattern pattern) => new(GlobSegmentKind.Pattern, null, pattern);
        public static GlobSegment MakeDoubleStar() => new(GlobSegmentKind.DoubleStar, null, null);
    }

    private Glob(GlobSegment[] segments)
    {
        _segments = segments;
    }

    /// <summary>
    /// Parses a glob pattern string into a <see cref="Glob"/>.
    /// Supports <c>*</c>, <c>?</c>, <c>[abc]</c> within path segments, and <c>**</c> for recursive directory matching.
    /// </summary>
    public static Glob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // Normalize separators
        var normalized = pattern.Replace('\\', '/');

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<GlobSegment>();

        foreach (var part in parts)
        {
            if (part == "**")
            {
                // Collapse consecutive **
                if (segments.Count == 0 || segments[^1].Kind != GlobSegmentKind.DoubleStar)
                    segments.Add(GlobSegment.MakeDoubleStar());
            }
            else if (!ContainsWildcard(part))
            {
                segments.Add(GlobSegment.MakeLiteral(part));
            }
            else
            {
                segments.Add(GlobSegment.MakePattern(WildcardPattern.Compile(part)));
            }
        }

        return new Glob(segments.ToArray());
    }

    /// <summary>
    /// Enumerates file paths matching this glob pattern relative to (and including) the base directory.
    /// </summary>
    public IEnumerable<string> EnumerateMatches(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? Directory.GetCurrentDirectory();

        if (_segments.Length == 0)
            yield break;

        foreach (var path in MatchSegments(baseDir, 0))
            yield return path;
    }

    /// <summary>
    /// Convenience: parse and enumerate in one call.
    /// </summary>
    public static IEnumerable<string> Match(string pattern, string? baseDirectory = null)
    {
        return Parse(pattern).EnumerateMatches(baseDirectory);
    }

    private IEnumerable<string> MatchSegments(string currentDir, int segmentIndex)
    {
        if (segmentIndex >= _segments.Length)
        {
            // All segments consumed — yield files at this location
            // This happens after ** consumed all remaining segments
            if (File.Exists(currentDir))
                yield return currentDir;
            yield break;
        }

        var seg = _segments[segmentIndex];
        bool isLast = segmentIndex == _segments.Length - 1;

        switch (seg.Kind)
        {
            case GlobSegmentKind.Literal:
            {
                var next = Path.Combine(currentDir, seg.LiteralName!);
                if (isLast)
                {
                    if (File.Exists(next))
                        yield return next;
                }
                else
                {
                    if (Directory.Exists(next))
                    {
                        foreach (var match in MatchSegments(next, segmentIndex + 1))
                            yield return match;
                    }
                }
                break;
            }

            case GlobSegmentKind.Pattern:
            {
                if (isLast)
                {
                    // Match files only
                    foreach (var file in EnumerateFilesSafe(currentDir))
                    {
                        if (seg.Pattern!.IsMatch(Path.GetFileName(file.AsSpan())))
                            yield return file;
                    }
                }
                else
                {
                    // Match directories to recurse into
                    foreach (var dir in EnumerateDirectoriesSafe(currentDir))
                    {
                        if (seg.Pattern!.IsMatch(Path.GetFileName(dir.AsSpan())))
                        {
                            foreach (var match in MatchSegments(dir, segmentIndex + 1))
                                yield return match;
                        }
                    }
                }
                break;
            }

            case GlobSegmentKind.DoubleStar:
            {
                // ** matches zero or more directory levels
                int nextSeg = segmentIndex + 1;

                // Zero levels: try matching the next segment from the current directory
                foreach (var match in MatchSegments(currentDir, nextSeg))
                    yield return match;

                // One or more levels: recurse into all subdirectories
                foreach (var subDir in EnumerateAllDirectoriesSafe(currentDir))
                {
                    foreach (var match in MatchSegments(subDir, nextSeg))
                        yield return match;
                }
                break;
            }
        }
    }

    private static bool ContainsWildcard(string segment)
    {
        foreach (char c in segment)
        {
            if (c is '*' or '?' or '[')
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try { return Directory.EnumerateFiles(directory); }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static IEnumerable<string> EnumerateAllDirectoriesSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }
}
