using System.Collections.Concurrent;
using System.IO.Enumeration;

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

    /// <summary>
    /// When true, follows symbolic links during traversal with cycle detection.
    /// When false (default), symbolic links are skipped — matching ripgrep behavior.
    /// </summary>
    public bool FollowSymlinks { get; init; }
}

/// <summary>
/// File system globber with support for <c>*</c>, <c>?</c>, <c>[abc]</c>, and <c>**</c> (recursive directory matching).
/// </summary>
public sealed class Glob
{
    private readonly GlobSegment[] _segments;
    private readonly string? _root; // non-null for absolute patterns
    private readonly Glob[]? _variants; // non-null for brace-expanded patterns

    private enum GlobSegmentKind : byte { Literal, Pattern, DoubleStar }

    private readonly struct GlobSegment
    {
        public readonly GlobSegmentKind Kind;
        public readonly string? LiteralName;
        public readonly WildcardPattern[]? Patterns;

        private GlobSegment(GlobSegmentKind kind, string? literal, WildcardPattern[]? patterns)
        {
            Kind = kind;
            LiteralName = literal;
            Patterns = patterns;
        }

        public bool IsPatternMatch(ReadOnlySpan<char> input)
        {
            var patterns = Patterns!;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (patterns[i].IsMatch(input))
                    return true;
            }
            return false;
        }

        public bool IsPatternMatch(string input)
        {
            var patterns = Patterns!;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (patterns[i].IsMatch(input))
                    return true;
            }
            return false;
        }

        public static GlobSegment MakeLiteral(string name) => new(GlobSegmentKind.Literal, name, null);
        public static GlobSegment MakePattern(WildcardPattern pattern) => new(GlobSegmentKind.Pattern, null, [pattern]);
        public static GlobSegment MakePattern(WildcardPattern[] patterns) => new(GlobSegmentKind.Pattern, null, patterns);
        public static GlobSegment MakeDoubleStar() => new(GlobSegmentKind.DoubleStar, null, null);
    }

    private readonly record struct DirEntry(string FullPath, bool IsSymlink);

    /// <summary>
    /// Tracks gitignore state and symlink cycle detection during directory traversal.
    /// Discovers git repos on-the-fly and manages per-repo ignore rules.
    /// </summary>
    private sealed class TraversalContext
    {
        public readonly bool DiscoverGitignore;
        public readonly bool FollowSymlinks;
        public GitignoreFilter? Filter;
        public string? GitRoot;
        private HashSet<string>? _visitedRealPaths;

        public TraversalContext(bool discoverGitignore, GitignoreFilter? filter, string? gitRoot,
            bool followSymlinks, HashSet<string>? visitedRealPaths = null)
        {
            DiscoverGitignore = discoverGitignore;
            Filter = filter;
            GitRoot = gitRoot;
            FollowSymlinks = followSymlinks;
            _visitedRealPaths = visitedRealPaths;
        }

        public void SeedVisitedPaths(string baseDirectory)
        {
            if (!FollowSymlinks) return;
            _visitedRealPaths = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(baseDirectory) };
        }

        /// <summary>
        /// Check if entering a symlinked directory would create a cycle.
        /// Returns true if safe to enter (not a cycle), false if cycle detected.
        /// </summary>
        public bool TryEnterSymlinkedDirectory(string directory)
        {
            if (_visitedRealPaths is null) return true;

            string realPath;
            try
            {
                var target = Directory.ResolveLinkTarget(directory, returnFinalTarget: true);
                realPath = target?.FullName ?? Path.GetFullPath(directory);
            }
            catch
            {
                realPath = Path.GetFullPath(directory);
            }

            return _visitedRealPaths.Add(realPath);
        }

        public void LeaveSymlinkedDirectory(string directory)
        {
            if (_visitedRealPaths is null) return;

            try
            {
                var target = Directory.ResolveLinkTarget(directory, returnFinalTarget: true);
                _visitedRealPaths.Remove(target?.FullName ?? Path.GetFullPath(directory));
            }
            catch
            {
                _visitedRealPaths.Remove(Path.GetFullPath(directory));
            }
        }

        public TraversalContext Clone()
        {
            return new TraversalContext(
                DiscoverGitignore,
                Filter?.Clone(),
                GitRoot,
                FollowSymlinks,
                _visitedRealPaths is not null ? new HashSet<string>(_visitedRealPaths, _visitedRealPaths.Comparer) : null);
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

    private Glob(GlobSegment[] segments, string? root, Glob[]? variants = null)
    {
        _segments = segments;
        _root = root;
        _variants = variants;
    }

    /// <summary>
    /// Parses a glob pattern string into a <see cref="Glob"/>.
    /// Supports <c>*</c>, <c>?</c>, <c>[abc]</c> within path segments, <c>**</c> for recursive directory matching,
    /// and <c>{a,b,c}</c> brace expansion for matching multiple alternatives.
    /// </summary>
    public static Glob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var expanded = BraceExpander.Expand(pattern);
        if (expanded.Length == 1)
            return ParseSingle(expanded[0]);

        var variants = new Glob[expanded.Length];
        for (int i = 0; i < expanded.Length; i++)
            variants[i] = ParseSingle(expanded[i]);

        // Optimization: if all variants share the same segment structure (same count,
        // same Kind/Literal for all but the last, and the last is Pattern in all),
        // merge into a single Glob with a multi-pattern final segment.
        // This avoids multiple filesystem traversals for patterns like **/*.{cs,razor,css}.
        var merged = TryMergeVariants(variants);
        if (merged is not null)
            return merged;

        return new Glob([], null, variants);
    }

    /// <summary>
    /// Attempts to merge variants that share the same segment structure into a single Glob
    /// with a multi-pattern final segment. Returns null if variants cannot be merged.
    /// </summary>
    private static Glob? TryMergeVariants(Glob[] variants)
    {
        if (variants.Length < 2)
            return null;

        var first = variants[0];
        int segCount = first._segments.Length;
        if (segCount == 0)
            return null;

        // All variants must have the same root and segment count
        for (int i = 1; i < variants.Length; i++)
        {
            if (variants[i]._segments.Length != segCount)
                return null;
            if (variants[i]._root != first._root)
                return null;
        }

        // Last segment must be Pattern in all variants
        if (first._segments[segCount - 1].Kind != GlobSegmentKind.Pattern)
            return null;
        for (int i = 1; i < variants.Length; i++)
        {
            if (variants[i]._segments[segCount - 1].Kind != GlobSegmentKind.Pattern)
                return null;
        }

        // All preceding segments must be identical across variants
        for (int s = 0; s < segCount - 1; s++)
        {
            var refSeg = first._segments[s];
            for (int v = 1; v < variants.Length; v++)
            {
                var otherSeg = variants[v]._segments[s];
                if (refSeg.Kind != otherSeg.Kind)
                    return null;
                if (refSeg.Kind == GlobSegmentKind.Literal && !string.Equals(refSeg.LiteralName, otherSeg.LiteralName, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (refSeg.Kind == GlobSegmentKind.Pattern)
                {
                    // Pattern segments must have same patterns — just bail if prefix has patterns
                    // (this optimization targets the common case: braces only in filename)
                    return null;
                }
            }
        }

        // Merge: collect all patterns from the last segment of each variant
        var allPatterns = new List<WildcardPattern>();
        for (int i = 0; i < variants.Length; i++)
        {
            var patterns = variants[i]._segments[segCount - 1].Patterns!;
            for (int j = 0; j < patterns.Length; j++)
                allPatterns.Add(patterns[j]);
        }

        var mergedSegments = new GlobSegment[segCount];
        Array.Copy(first._segments, mergedSegments, segCount - 1);
        mergedSegments[segCount - 1] = GlobSegment.MakePattern(allPatterns.ToArray());

        return new Glob(mergedSegments, first._root);
    }

    private static Glob ParseSingle(string pattern)
    {
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
        if (_variants is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variant in _variants)
            {
                foreach (var path in variant.EnumerateMatches(baseDirectory, options))
                {
                    if (seen.Add(path))
                        yield return path;
                }
            }
            yield break;
        }

        var baseDir = _root ?? (baseDirectory is not null ? Path.GetFullPath(baseDirectory) : null) ?? Directory.GetCurrentDirectory();

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

        bool followSymlinks = options?.FollowSymlinks == true;
        var ctx = new TraversalContext(respectGitignore, filter, gitRoot, followSymlinks);
        if (followSymlinks) ctx.SeedVisitedPaths(baseDir);

        // Deduplicate only when multiple ** segments can cause the same file to be reached
        // via different traversal paths. Single-** patterns form a tree walk — no duplicates possible.
        int doubleStarCount = 0;
        foreach (var seg in _segments)
            if (seg.Kind == GlobSegmentKind.DoubleStar) doubleStarCount++;

        if (doubleStarCount >= 2)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in MatchSegments(baseDir, 0, ctx))
                if (seen.Add(path)) yield return path;
        }
        else
        {
            foreach (var path in MatchSegments(baseDir, 0, ctx))
                yield return path;
        }
    }

    /// <summary>
    /// Convenience: parse and enumerate in one call.
    /// </summary>
    public static IEnumerable<string> Match(string pattern, string? baseDirectory = null, GlobOptions? options = null)
    {
        return Parse(pattern).EnumerateMatches(baseDirectory, options);
    }

    /// <summary>
    /// Writes matching file paths to a channel writer using parallel directory walking.
    /// Parallelizes subtree traversal at <c>**</c> boundaries for maximum throughput.
    /// </summary>
    public void WriteMatchesToChannel(System.Threading.Channels.ChannelWriter<string> writer,
        string? baseDirectory = null, GlobOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_variants is not null)
        {
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var deduplicatingWriter = new DeduplicatingChannelWriter(writer, seen);
            foreach (var variant in _variants)
                variant.WriteMatchesToChannel(deduplicatingWriter, baseDirectory, options, cancellationToken);
            return;
        }

        var baseDir = _root ?? (baseDirectory is not null ? Path.GetFullPath(baseDirectory) : null) ?? Directory.GetCurrentDirectory();
        if (_segments.Length == 0) return;

        GitignoreFilter? filter = null;
        string? gitRoot = null;
        bool respectGitignore = options?.RespectGitignore == true;
        if (respectGitignore)
        {
            gitRoot = GitignoreFilter.FindGitRoot(baseDir);
            if (gitRoot is not null)
                filter = GitignoreFilter.LoadFromGitRoot(gitRoot);
        }

        bool followSymlinks = options?.FollowSymlinks == true;
        var ctx = new TraversalContext(respectGitignore, filter, gitRoot, followSymlinks);
        if (followSymlinks) ctx.SeedVisitedPaths(baseDir);

        // Patterns with multiple ** segments can match the same file via different traversal paths
        // (e.g. **/a/**/*.cs visits a file in nested `a` dirs twice). Deduplicate only then —
        // single-** patterns form a tree walk and can never produce duplicates.
        int doubleStarCount = 0;
        foreach (var seg in _segments)
            if (seg.Kind == GlobSegmentKind.DoubleStar) doubleStarCount++;

        if (doubleStarCount >= 2)
        {
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            writer = new DeduplicatingChannelWriter(writer, seen);
        }

        WriteMatchesSegments(baseDir, 0, ctx, writer, cancellationToken);
    }

    /// <summary>
    /// Convenience: parse and write matches to a channel in one call.
    /// </summary>
    public static void MatchToChannel(string pattern, System.Threading.Channels.ChannelWriter<string> writer,
        string? baseDirectory = null, GlobOptions? options = null, CancellationToken cancellationToken = default)
    {
        Parse(pattern).WriteMatchesToChannel(writer, baseDirectory, options, cancellationToken);
    }

    /// <summary>
    /// Writes a value to the channel writer, blocking if the channel is full.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void WriteBlocking(System.Threading.Channels.ChannelWriter<string> writer, string value)
    {
        while (!writer.TryWrite(value))
            writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult();
    }

    private void WriteMatchesSegments(string currentDir, int segmentIndex, TraversalContext ctx,
        System.Threading.Channels.ChannelWriter<string> writer, CancellationToken ct)
    {
        bool enteredRepo = ctx.TryEnterGitRepo(currentDir);
        int addedRules = ctx.EnterDirectory(currentDir);
        try
        {
            WriteMatchesSegmentsCore(currentDir, segmentIndex, ctx, writer, ct);
        }
        finally
        {
            ctx.LeaveDirectory(addedRules);
            if (enteredRepo) ctx.LeaveGitRepo();
        }
    }

    private void WriteMatchesSegmentsCore(string currentDir, int segmentIndex, TraversalContext ctx,
        System.Threading.Channels.ChannelWriter<string> writer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (segmentIndex >= _segments.Length)
        {
            if (File.Exists(currentDir))
                WriteBlocking(writer,currentDir);
            else if (Directory.Exists(currentDir))
            {
                foreach (var file in EnumerateFilesSafe(currentDir, ctx.FollowSymlinks))
                {
                    if (ctx.IsIgnored(file, false)) continue;
                    WriteBlocking(writer,file);
                }
            }
            return;
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
                        WriteBlocking(writer,next);
                }
                else if (Directory.Exists(next) && !ctx.IsIgnored(next, true))
                {
                    WriteMatchesSegments(next, segmentIndex + 1, ctx, writer, ct);
                }
                break;
            }

            case GlobSegmentKind.Pattern:
            {
                if (isLast)
                {
                    foreach (var file in EnumerateFilesSafe(currentDir, ctx.FollowSymlinks))
                    {
                        if (seg.IsPatternMatch(Path.GetFileName(file.AsSpan())))
                        {
                            if (!ctx.IsIgnored(file, false))
                                WriteBlocking(writer,file);
                        }
                    }
                }
                else
                {
                    foreach (var entry in EnumerateDirectoriesSafe(currentDir, ctx.FollowSymlinks))
                    {
                        if (seg.IsPatternMatch(Path.GetFileName(entry.FullPath.AsSpan())) && !ctx.IsIgnored(entry.FullPath, true))
                        {
                            if (entry.IsSymlink && !ctx.TryEnterSymlinkedDirectory(entry.FullPath)) continue;
                            try
                            {
                                WriteMatchesSegments(entry.FullPath, segmentIndex + 1, ctx, writer, ct);
                            }
                            finally
                            {
                                if (entry.IsSymlink) ctx.LeaveSymlinkedDirectory(entry.FullPath);
                            }
                        }
                    }
                }
                break;
            }

            case GlobSegmentKind.DoubleStar:
            {
                int nextSeg = segmentIndex + 1;

                // Zero levels: match next segment from current directory
                WriteMatchesSegmentsCore(currentDir, nextSeg, ctx, writer, ct);

                // Work-stealing parallel walk: seed a shared queue with immediate subdirectories,
                // fixed worker pool processes each dir and re-enqueues discovered subdirs.
                // This distributes work across all cores even for deep/unbalanced trees,
                // unlike the previous approach which forked once and went sequential per subtree.
                var subDirs = EnumerateDirectoriesSafe(currentDir, ctx.FollowSymlinks)
                    .Where(e => !ctx.IsIgnored(e.FullPath, true))
                    .ToArray();

                if (subDirs.Length == 0) break;

                var workQueue = new ConcurrentQueue<(string Dir, TraversalContext Ctx)>();
                int outstandingWork = 0;
                using var done = new ManualResetEventSlim(false);

                // Seed with immediate subdirectories — each gets an independent context clone
                foreach (var entry in subDirs)
                {
                    var localCtx = ctx.Clone();
                    if (entry.IsSymlink && !localCtx.TryEnterSymlinkedDirectory(entry.FullPath)) continue;
                    localCtx.TryEnterGitRepo(entry.FullPath);
                    localCtx.EnterDirectory(entry.FullPath);
                    Interlocked.Increment(ref outstandingWork);
                    workQueue.Enqueue((entry.FullPath, localCtx));
                }

                if (outstandingWork == 0) break;

                int workerCount = Environment.ProcessorCount;
                var workers = new Task[workerCount];
                for (int w = 0; w < workerCount; w++)
                {
                    workers[w] = Task.Run(() =>
                    {
                        var spinner = new SpinWait();
                        while (!done.IsSet)
                        {
                            if (workQueue.TryDequeue(out var item))
                            {
                                spinner.Reset();
                                var (dir, dirCtx) = item;
                                try
                                {
                                    // Match next segment from this directory
                                    WriteMatchesSegmentsCore(dir, nextSeg, dirCtx, writer, ct);

                                    // Discover subdirectories and enqueue for any idle worker to pick up
                                    foreach (var subEntry in EnumerateDirectoriesSafe(dir, dirCtx.FollowSymlinks))
                                    {
                                        if (dirCtx.IsIgnored(subEntry.FullPath, true)) continue;
                                        var subCtx = dirCtx.Clone();
                                        if (subEntry.IsSymlink && !subCtx.TryEnterSymlinkedDirectory(subEntry.FullPath)) continue;
                                        subCtx.TryEnterGitRepo(subEntry.FullPath);
                                        subCtx.EnterDirectory(subEntry.FullPath);
                                        Interlocked.Increment(ref outstandingWork);
                                        workQueue.Enqueue((subEntry.FullPath, subCtx));
                                    }
                                }
                                finally
                                {
                                    if (Interlocked.Decrement(ref outstandingWork) == 0)
                                        done.Set();
                                }
                            }
                            else
                            {
                                spinner.SpinOnce();
                            }
                        }
                    }, ct);
                }

                done.Wait(ct);
                Task.WaitAll(workers);
                break;
            }
        }
    }

    /// <summary>
    /// Tests whether a relative path matches this glob pattern without touching the filesystem.
    /// Path separators are normalized to forward slashes before matching.
    /// </summary>
    public bool IsMatch(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (_variants is not null)
        {
            foreach (var variant in _variants)
            {
                if (variant.IsMatch(relativePath))
                    return true;
            }
            return false;
        }

        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return IsMatchSegments(parts, 0, 0);
    }

    /// <summary>
    /// Convenience: parse a glob pattern and test a relative path in one call.
    /// </summary>
    public static bool IsMatch(string pattern, string relativePath)
    {
        return Parse(pattern).IsMatch(relativePath);
    }

    private bool IsMatchSegments(string[] parts, int segmentIndex, int partIndex)
    {
        // All segments consumed — path must also be fully consumed
        if (segmentIndex >= _segments.Length)
            return partIndex >= parts.Length;

        var seg = _segments[segmentIndex];

        switch (seg.Kind)
        {
            case GlobSegmentKind.Literal:
                if (partIndex >= parts.Length) return false;
                if (!string.Equals(seg.LiteralName, parts[partIndex], StringComparison.OrdinalIgnoreCase))
                    return false;
                return IsMatchSegments(parts, segmentIndex + 1, partIndex + 1);

            case GlobSegmentKind.Pattern:
                if (partIndex >= parts.Length) return false;
                if (!seg.IsPatternMatch(parts[partIndex]))
                    return false;
                return IsMatchSegments(parts, segmentIndex + 1, partIndex + 1);

            case GlobSegmentKind.DoubleStar:
                // ** can match zero or more path segments
                int nextSeg = segmentIndex + 1;
                for (int i = partIndex; i <= parts.Length; i++)
                {
                    if (IsMatchSegments(parts, nextSeg, i))
                        return true;
                }
                return false;

            default:
                return false;
        }
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
                foreach (var file in EnumerateFilesSafe(currentDir, ctx.FollowSymlinks))
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
                    foreach (var file in EnumerateFilesSafe(currentDir, ctx.FollowSymlinks))
                    {
                        if (seg.IsPatternMatch(Path.GetFileName(file.AsSpan())))
                        {
                            if (!ctx.IsIgnored(file, false))
                                yield return file;
                        }
                    }
                }
                else
                {
                    foreach (var entry in EnumerateDirectoriesSafe(currentDir, ctx.FollowSymlinks))
                    {
                        if (seg.IsPatternMatch(Path.GetFileName(entry.FullPath.AsSpan())))
                        {
                            if (!ctx.IsIgnored(entry.FullPath, true))
                            {
                                if (entry.IsSymlink && !ctx.TryEnterSymlinkedDirectory(entry.FullPath)) continue;
                                try
                                {
                                    foreach (var match in MatchSegments(entry.FullPath, segmentIndex + 1, ctx))
                                        yield return match;
                                }
                                finally
                                {
                                    if (entry.IsSymlink) ctx.LeaveSymlinkedDirectory(entry.FullPath);
                                }
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
                foreach (var entry in EnumerateDirectoriesSafe(currentDir, ctx.FollowSymlinks))
                {
                    if (ctx.IsIgnored(entry.FullPath, true)) continue;
                    if (entry.IsSymlink && !ctx.TryEnterSymlinkedDirectory(entry.FullPath)) continue;

                    // Check if subdir is a new git repo
                    bool enteredRepo = ctx.TryEnterGitRepo(entry.FullPath);
                    int added = ctx.EnterDirectory(entry.FullPath);
                    try
                    {
                        // Try matching next segment from this subdir
                        foreach (var match in MatchSegmentsCore(entry.FullPath, nextSeg, ctx))
                            yield return match;

                        // Continue recursing deeper
                        foreach (var deepDir in EnumerateAllSubdirectoriesFiltered(entry.FullPath, ctx))
                        {
                            foreach (var match in MatchSegmentsCore(deepDir, nextSeg, ctx))
                                yield return match;
                        }
                    }
                    finally
                    {
                        ctx.LeaveDirectory(added);
                        if (enteredRepo) ctx.LeaveGitRepo();
                        if (entry.IsSymlink) ctx.LeaveSymlinkedDirectory(entry.FullPath);
                    }
                }
                break;
            }
        }
    }

    private static IEnumerable<string> EnumerateAllSubdirectoriesFiltered(
        string directory, TraversalContext ctx)
    {
        if (!ctx.DiscoverGitignore && ctx.Filter is null && !ctx.FollowSymlinks)
        {
            // Fast path: no filtering needed, symlinks skipped via ShouldRecurse predicate
            foreach (var dir in EnumerateAllDirectoriesSafe(directory))
                yield return dir;
            yield break;
        }

        // Manual recursive walk for gitignore filtering and symlink cycle detection
        foreach (var entry in EnumerateDirectoriesSafe(directory, ctx.FollowSymlinks))
        {
            if (ctx.IsIgnored(entry.FullPath, true)) continue;
            if (entry.IsSymlink && !ctx.TryEnterSymlinkedDirectory(entry.FullPath)) continue;

            bool enteredRepo = ctx.TryEnterGitRepo(entry.FullPath);
            int added = ctx.EnterDirectory(entry.FullPath);
            try
            {
                yield return entry.FullPath;

                foreach (var deeper in EnumerateAllSubdirectoriesFiltered(entry.FullPath, ctx))
                    yield return deeper;
            }
            finally
            {
                ctx.LeaveDirectory(added);
                if (enteredRepo) ctx.LeaveGitRepo();
                if (entry.IsSymlink) ctx.LeaveSymlinkedDirectory(entry.FullPath);
            }
        }
    }

    private static IEnumerable<string> EnumerateAllDirectoriesSafe(string directory)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = true,
            };
            return new FileSystemEnumerable<string>(directory,
                static (ref FileSystemEntry entry) => entry.ToFullPath(),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                    entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static bool ContainsWildcard(string segment)
    {
        foreach (char c in segment)
        {
            if (c is '*' or '?' or '[' or '{')
                return true;
        }
        return false;
    }

    private static readonly EnumerationOptions SkipSymlinksOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions FollowSymlinksOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private static IEnumerable<string> EnumerateFilesSafe(string directory, bool followSymlinks)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*",
                followSymlinks ? FollowSymlinksOptions : SkipSymlinksOptions);
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static IEnumerable<DirEntry> EnumerateDirectoriesSafe(string directory, bool followSymlinks)
    {
        try
        {
            if (followSymlinks)
            {
                // Need FileSystemEnumerable to detect which directories are symlinks for cycle detection
                return new FileSystemEnumerable<DirEntry>(directory,
                    static (ref FileSystemEntry entry) =>
                        new DirEntry(entry.ToFullPath(), (entry.Attributes & FileAttributes.ReparsePoint) != 0),
                    FollowSymlinksOptions)
                {
                    ShouldIncludePredicate = static (ref FileSystemEntry entry) => entry.IsDirectory
                };
            }

            // Not following symlinks — use fast Directory.EnumerateDirectories, wrap as DirEntry(IsSymlink: false)
            return Directory.EnumerateDirectories(directory, "*", SkipSymlinksOptions)
                .Select(static d => new DirEntry(d, false));
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    /// <summary>
    /// A channel writer wrapper that deduplicates values using a concurrent dictionary.
    /// Used when multiple brace-expanded variants write to the same channel.
    /// </summary>
    private sealed class DeduplicatingChannelWriter(
        System.Threading.Channels.ChannelWriter<string> inner,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte> seen)
        : System.Threading.Channels.ChannelWriter<string>
    {
        public override bool TryWrite(string item)
        {
            if (!seen.TryAdd(item, 0))
                return true; // already written by another variant — skip
            return inner.TryWrite(item);
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);
    }
}
