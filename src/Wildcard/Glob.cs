namespace Wildcard;

/// <summary>
/// Options for glob matching.
/// </summary>
public sealed class GlobOptions
{
    /// <summary>
    /// When true, respects .gitignore files to skip ignored files and directories.
    /// Automatically detects git repositories during traversal.
    /// Default: false.
    /// </summary>
    public bool RespectGitignore { get; init; }
}

/// <summary>
/// File system globber with support for <c>*</c>, <c>?</c>, <c>[abc]</c>, and <c>**</c> (recursive directory matching).
/// </summary>
public sealed class Glob
{
    private readonly GlobSegment[] _segments;
    private readonly string? _root; // non-null for absolute patterns

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

    /// <summary>
    /// Tracks gitignore state during directory traversal.
    /// Discovers git repos on-the-fly and manages per-repo ignore rules.
    /// </summary>
    private sealed class TraversalContext
    {
        public readonly bool DiscoverGitignore;
        public GitignoreFilter? Filter;
        public string? GitRoot;

        public TraversalContext(bool discoverGitignore, GitignoreFilter? filter, string? gitRoot)
        {
            DiscoverGitignore = discoverGitignore;
            Filter = filter;
            GitRoot = gitRoot;
        }

        /// <summary>
        /// Check if a directory is a git root and activate filtering.
        /// Returns true if a new git repo was discovered (caller must call LeaveGitRepo on exit).
        /// </summary>
        public bool TryEnterGitRepo(string directory)
        {
            if (!DiscoverGitignore || Filter is not null) return false;

            if (!Directory.Exists(Path.Combine(directory, ".git"))) return false;

            GitRoot = directory;
            Filter = GitignoreFilter.LoadFromGitRoot(directory) ?? new GitignoreFilter();
            return true;
        }

        public void LeaveGitRepo()
        {
            Filter = null;
            GitRoot = null;
        }

        /// <summary>
        /// Load nested .gitignore rules when entering a subdirectory within a repo.
        /// Returns rule count for later removal.
        /// </summary>
        public int EnterDirectory(string directory)
        {
            return Filter?.AddRulesFromDirectory(directory) ?? 0;
        }

        public void LeaveDirectory(int addedRules)
        {
            if (addedRules > 0) Filter!.RemoveLastRules(addedRules);
        }

        public bool IsIgnored(string fullPath, bool isDirectory)
        {
            if (!DiscoverGitignore) return false;

            // Always skip .git directories — git never lists them in .gitignore
            if (isDirectory && Path.GetFileName(fullPath.AsSpan()).SequenceEqual(".git"))
                return true;

            if (Filter is null || GitRoot is null) return false;
            var relativePath = Path.GetRelativePath(GitRoot, fullPath).Replace('\\', '/');
            return Filter.IsIgnored(relativePath, isDirectory);
        }
    }

    private Glob(GlobSegment[] segments, string? root)
    {
        _segments = segments;
        _root = root;
    }

    /// <summary>
    /// Parses a glob pattern string into a <see cref="Glob"/>.
    /// Supports <c>*</c>, <c>?</c>, <c>[abc]</c> within path segments, and <c>**</c> for recursive directory matching.
    /// </summary>
    public static Glob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // Detect absolute path root before normalizing
        string? root = null;
        if (Path.IsPathRooted(pattern))
            root = Path.GetPathRoot(pattern);

        // Normalize separators
        var normalized = pattern.Replace('\\', '/');

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<GlobSegment>();

        foreach (var part in parts)
        {
            // Skip the drive letter segment on Windows (e.g. "C:") — it's captured in root
            if (root is not null && segments.Count == 0 && part.Length == 2 && part[1] == ':')
                continue;

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

        return new Glob(segments.ToArray(), root);
    }

    /// <summary>
    /// Enumerates file paths matching this glob pattern relative to (and including) the base directory.
    /// </summary>
    public IEnumerable<string> EnumerateMatches(string? baseDirectory = null, GlobOptions? options = null)
    {
        var baseDir = _root ?? baseDirectory ?? Directory.GetCurrentDirectory();

        if (_segments.Length == 0)
            yield break;

        // If we're already inside a git repo, load it upfront
        GitignoreFilter? filter = null;
        string? gitRoot = null;
        bool respectGitignore = options?.RespectGitignore == true;
        if (respectGitignore)
        {
            gitRoot = GitignoreFilter.FindGitRoot(baseDir);
            if (gitRoot is not null)
                filter = GitignoreFilter.LoadFromGitRoot(gitRoot);
        }

        var ctx = new TraversalContext(respectGitignore, filter, gitRoot);
        foreach (var path in MatchSegments(baseDir, 0, ctx))
            yield return path;
    }

    /// <summary>
    /// Convenience: parse and enumerate in one call.
    /// </summary>
    public static IEnumerable<string> Match(string pattern, string? baseDirectory = null, GlobOptions? options = null)
    {
        return Parse(pattern).EnumerateMatches(baseDirectory, options);
    }

    private IEnumerable<string> MatchSegments(string currentDir, int segmentIndex, TraversalContext ctx)
    {
        // Check if we're entering a new git repo
        bool enteredRepo = ctx.TryEnterGitRepo(currentDir);

        // Load nested .gitignore rules
        int addedRules = ctx.EnterDirectory(currentDir);

        try
        {
            foreach (var result in MatchSegmentsCore(currentDir, segmentIndex, ctx))
                yield return result;
        }
        finally
        {
            ctx.LeaveDirectory(addedRules);
            if (enteredRepo) ctx.LeaveGitRepo();
        }
    }

    private IEnumerable<string> MatchSegmentsCore(string currentDir, int segmentIndex, TraversalContext ctx)
    {
        if (segmentIndex >= _segments.Length)
        {
            // All segments consumed — yield files at this location
            if (File.Exists(currentDir))
                yield return currentDir;
            else if (Directory.Exists(currentDir))
            {
                foreach (var file in EnumerateFilesSafe(currentDir))
                {
                    if (ctx.IsIgnored(file, false)) continue;
                    yield return file;
                }
            }
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
                    if (File.Exists(next) && !ctx.IsIgnored(next, false))
                        yield return next;
                }
                else
                {
                    if (Directory.Exists(next) && !ctx.IsIgnored(next, true))
                    {
                        foreach (var match in MatchSegments(next, segmentIndex + 1, ctx))
                            yield return match;
                    }
                }
                break;
            }

            case GlobSegmentKind.Pattern:
            {
                if (isLast)
                {
                    foreach (var file in EnumerateFilesSafe(currentDir))
                    {
                        if (seg.Pattern!.IsMatch(Path.GetFileName(file.AsSpan())))
                        {
                            if (!ctx.IsIgnored(file, false))
                                yield return file;
                        }
                    }
                }
                else
                {
                    foreach (var dir in EnumerateDirectoriesSafe(currentDir))
                    {
                        if (seg.Pattern!.IsMatch(Path.GetFileName(dir.AsSpan())))
                        {
                            if (!ctx.IsIgnored(dir, true))
                            {
                                foreach (var match in MatchSegments(dir, segmentIndex + 1, ctx))
                                    yield return match;
                            }
                        }
                    }
                }
                break;
            }

            case GlobSegmentKind.DoubleStar:
            {
                int nextSeg = segmentIndex + 1;

                // Zero levels: try matching the next segment from the current directory
                foreach (var match in MatchSegmentsCore(currentDir, nextSeg, ctx))
                    yield return match;

                // One or more levels: recurse into subdirectories
                foreach (var subDir in EnumerateDirectoriesSafe(currentDir))
                {
                    if (ctx.IsIgnored(subDir, true)) continue;

                    // Check if subdir is a new git repo
                    bool enteredRepo = ctx.TryEnterGitRepo(subDir);
                    int added = ctx.EnterDirectory(subDir);
                    try
                    {
                        // Try matching next segment from this subdir
                        foreach (var match in MatchSegmentsCore(subDir, nextSeg, ctx))
                            yield return match;

                        // Continue recursing deeper
                        foreach (var deepDir in EnumerateAllSubdirectoriesFiltered(subDir, ctx))
                        {
                            foreach (var match in MatchSegmentsCore(deepDir, nextSeg, ctx))
                                yield return match;
                        }
                    }
                    finally
                    {
                        ctx.LeaveDirectory(added);
                        if (enteredRepo) ctx.LeaveGitRepo();
                    }
                }
                break;
            }
        }
    }

    private static IEnumerable<string> EnumerateAllSubdirectoriesFiltered(
        string directory, TraversalContext ctx)
    {
        if (!ctx.DiscoverGitignore && ctx.Filter is null)
        {
            // No filtering — use fast built-in recursive enumeration
            foreach (var dir in EnumerateAllDirectoriesSafe(directory))
                yield return dir;
            yield break;
        }

        // Manual recursive walk so we can skip ignored directories early
        foreach (var subDir in EnumerateDirectoriesSafe(directory))
        {
            if (ctx.IsIgnored(subDir, true)) continue;

            bool enteredRepo = ctx.TryEnterGitRepo(subDir);
            int added = ctx.EnterDirectory(subDir);
            try
            {
                yield return subDir;

                foreach (var deeper in EnumerateAllSubdirectoriesFiltered(subDir, ctx))
                    yield return deeper;
            }
            finally
            {
                ctx.LeaveDirectory(added);
                if (enteredRepo) ctx.LeaveGitRepo();
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
