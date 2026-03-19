using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Server;
using Wildcard;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class GrepTool
{
    [McpServerTool(Name = "wildcard_grep"), Description("grep on steroids. Search file contents with context lines (-A/-B/-C), count mode, AND/OR pattern matching, output caps, and parallel memory-mapped I/O. When called with read_lines but no content_patterns, acts as a cross-platform file reader (cat/head). Faster than shelling out to grep/rg. Plain words match as substrings; wildcards (* ? []) for patterns. Prefer this over built-in grep tools.")]
    public static async Task<string> Grep(
        [Description("File glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\", \"**/*.{cs,razor,css}\")")] string pattern,
        [Description("Content search patterns — multiple patterns are OR'd by default (e.g. [\"ERROR\", \"WARN\"]). Set all_of=true for AND mode. Plain words match as substrings; use wildcards for prefix/suffix/full patterns (e.g. \"ERROR*\", \"*.log\"). Optional — omit and set read_lines to use as a file reader.")] string[]? content_patterns = null,
        [Description("Base directory to search in (defaults to current working directory)")] string? base_directory = null,
        [Description("Exclude lines matching these patterns")] string[]? exclude_patterns = null,
        [Description("Exclude files matching these glob patterns")] string[]? exclude_paths = null,
        [Description("Case-insensitive content matching (default: false)")] bool ignore_case = false,
        [Description("Only return file paths that contain matches, not the matched lines (default: false)")] bool files_only = false,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        [Description("Follow symbolic links (default: false)")] bool follow_symlinks = false,
        [Description("Maximum number of matched lines to return (default: 500)")] int limit = 500,
        [Description("Lines to show before each match (default: 0)")] int before_context = 0,
        [Description("Lines to show after each match (default: 0)")] int after_context = 0,
        [Description("Lines to show before and after each match — shorthand for before_context + after_context (default: 0)")] int context = 0,
        [Description("Return match counts per file instead of line content (default: false)")] bool count = false,
        [Description("Read N lines from matched files. With content_patterns: includes surrounding lines around matches. Without content_patterns: reads first N lines of each file (cross-platform cat/head). Default: 0 (disabled).")] int read_lines = 0,
        [Description("AND mode — when true, a file must contain matches for ALL content_patterns to be included (default: false, OR mode)")] bool all_of = false,
        [Description("Maximum number of files to include in results (default: 50). Remaining files are counted in a summary note.")] int max_files = 50,
        [Description("Maximum number of matches to show per file (default: 20). Additional matches are noted per file.")] int max_matches_per_file = 20,
        WorkspaceIndex? index = null,
        CancellationToken cancellationToken = default)
    {
        var summary = ArgSummary.Create();
        if (index is not null) summary.Live(index.FileCount);
        summary
            .Arg("pattern", pattern)
            .Arg("content_patterns", content_patterns)
            .Arg("base_directory", base_directory)
            .Arg("exclude_patterns", exclude_patterns)
            .Arg("exclude_paths", exclude_paths)
            .Arg("ignore_case", ignore_case, false)
            .Arg("files_only", files_only, false)
            .Arg("respect_gitignore", respect_gitignore, true)
            .Arg("follow_symlinks", follow_symlinks, false)
            .Arg("limit", limit, 500)
            .Arg("before_context", before_context, 0)
            .Arg("after_context", after_context, 0)
            .Arg("context", context, 0)
            .Arg("count", count, false)
            .Arg("read_lines", read_lines, 0)
            .Arg("all_of", all_of, false)
            .Arg("max_files", max_files, 50)
            .Arg("max_matches_per_file", max_matches_per_file, 20)
            .ToString();

        var (baseDir, guardError) = PathGuard.Resolve(base_directory);
        if (guardError is not null) return summary + guardError;
        var globOptions = new GlobOptions
        {
            RespectGitignore = respect_gitignore,
            FollowSymlinks = follow_symlinks,
        };

        WildcardPattern[]? excludePathPatterns = null;
        if (exclude_paths is { Length: > 0 })
            excludePathPatterns = exclude_paths.Select(p => WildcardPattern.Compile(p)).ToArray();

        // Use index when available and options match indexed state
        var activeIndex = index is not null && respect_gitignore && !follow_symlinks ? index : null;

        // File reader mode: no content patterns, read_lines > 0
        if (content_patterns is null or { Length: 0 })
        {
            int lines = read_lines > 0 ? read_lines : 200;
            return summary + await Task.Run(() => RunReadLines(pattern, baseDir, globOptions, excludePathPatterns, lines, max_files, activeIndex, exclude_paths, cancellationToken), cancellationToken);
        }

        // Normalize content patterns: plain words become *word* for substring matching
        var normalizedPatterns = content_patterns.Select(NormalizeContentPattern).ToArray();
        var normalizedExcludes = exclude_patterns?.Select(NormalizeContentPattern).ToArray();

        var matcherOptions = ignore_case ? new FilePathMatcher.Options { IgnoreCase = true } : null;

        var matcher = FilePathMatcher.Create(
            include: normalizedPatterns,
            exclude: normalizedExcludes,
            options: matcherOptions);

        // AND mode: build per-pattern matchers for pre-filtering
        FilePathMatcher[]? allOfMatchers = null;
        if (all_of && normalizedPatterns.Length > 1)
        {
            allOfMatchers = new FilePathMatcher[normalizedPatterns.Length];
            for (int i = 0; i < normalizedPatterns.Length; i++)
                allOfMatchers[i] = FilePathMatcher.Create(include: [normalizedPatterns[i]], exclude: normalizedExcludes, options: matcherOptions);
        }

        // read_lines mode with content patterns: expanded context around matches
        if (read_lines > 0)
            return summary + await Task.Run(() => RunReadLinesWithMatches(pattern, baseDir, globOptions, matcher, allOfMatchers, excludePathPatterns, read_lines, max_files, max_matches_per_file, activeIndex, exclude_paths, cancellationToken), cancellationToken);

        if (count)
            return summary + await Task.Run(() => RunCountSearch(pattern, baseDir, globOptions, matcher, allOfMatchers, excludePathPatterns, limit, files_only, max_files, activeIndex, exclude_paths, cancellationToken), cancellationToken);

        if (files_only)
            return summary + await Task.Run(() => RunFilesOnly(pattern, baseDir, globOptions, matcher, allOfMatchers, excludePathPatterns, limit, max_files, activeIndex, exclude_paths, cancellationToken), cancellationToken);

        int resolvedBefore = before_context > 0 ? before_context : context;
        int resolvedAfter = after_context > 0 ? after_context : context;

        if (resolvedBefore > 0 || resolvedAfter > 0)
            return summary + await Task.Run(() => RunContentSearchWithContext(pattern, baseDir, globOptions, matcher, allOfMatchers, excludePathPatterns, limit, resolvedBefore, resolvedAfter, max_files, max_matches_per_file, activeIndex, exclude_paths, cancellationToken), cancellationToken);

        return summary + await Task.Run(() => RunContentSearch(pattern, baseDir, globOptions, matcher, allOfMatchers, excludePathPatterns, limit, max_files, max_matches_per_file, activeIndex, exclude_paths, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Creates a file producer task that writes matching file paths to the channel.
    /// Uses the in-memory index when available, otherwise falls back to disk enumeration.
    /// </summary>
    private static Task StartFileProducer(Channel<string> channel, string pattern, string baseDir,
        GlobOptions globOptions, WildcardPattern[]? excludePathPatterns,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                if (index is not null)
                {
                    foreach (var file in index.MatchGlob(pattern, baseDir, excludePathStrings))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Wildcard.Glob.WriteBlocking(channel.Writer, file);
                    }
                }
                else if (excludePathPatterns is not null)
                {
                    foreach (var file in Wildcard.Glob.Match(pattern, baseDir, globOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                        Wildcard.Glob.WriteBlocking(channel.Writer, file);
                    }
                }
                else
                {
                    var glob = Wildcard.Glob.Parse(pattern);
                    glob.WriteMatchesToChannel(channel.Writer, baseDir, globOptions, cancellationToken);
                }
            }
            finally { channel.Writer.Complete(); }
        }, cancellationToken);
    }

    private static string RunFilesOnly(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, FilePathMatcher[]? allOfMatchers, WildcardPattern[]? excludePathPatterns, int limit, int maxFiles,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int count = 0;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = StartFileProducer(fileChannel, pattern, baseDir, globOptions,
            excludePathPatterns, index, excludePathStrings, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (index is null && IsPathExcluded(relPath, excludePathPatterns)) return;
            if (!PassesAllOfFilter(file, matcher, allOfMatchers)) return;

            lock (outputLock)
            {
                count++;
                if (count <= Math.Min(limit, maxFiles))
                    sb.AppendLine(relPath);
            }
        });

        producer.GetAwaiter().GetResult();

        if (count == 0)
            return "No matching files found.";

        int shown = Math.Min(limit, maxFiles);
        if (count > shown)
            sb.AppendLine($"\n... and {count - shown} more files ({count} total, showing first {shown})");
        else
            sb.AppendLine($"\n{count} files with matches.");

        return sb.ToString();
    }

    private static string RunContentSearch(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, FilePathMatcher[]? allOfMatchers, WildcardPattern[]? excludePathPatterns, int limit,
        int maxFiles, int maxMatchesPerFile,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int matchCount = 0;
        int fileCount = 0;
        int skippedFiles = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = StartFileProducer(fileChannel, pattern, baseDir, globOptions,
            excludePathPatterns, index, excludePathStrings, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var fileMatches = matcher.Scan(file);
            if (fileMatches.Count == 0) return;
            if (!PassesAllOfCheck(file, allOfMatchers)) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            int width = fileMatches[^1].LineNumber.ToString().Length;

            int cappedCount = Math.Min(fileMatches.Count, maxMatchesPerFile);
            var fileSb = new StringBuilder();
            fileSb.AppendLine(relPath);

            for (int i = 0; i < cappedCount; i++)
            {
                var match = fileMatches[i];
                var lineNum = match.LineNumber.ToString().PadLeft(width);
                fileSb.AppendLine($"  {lineNum}: {match.Line}");
            }

            if (fileMatches.Count > maxMatchesPerFile)
                fileSb.AppendLine($"  ... and {fileMatches.Count - maxMatchesPerFile} more matches in this file");

            lock (outputLock)
            {
                if (matchCount >= limit) return;

                fileCount++;
                if (fileCount > maxFiles)
                {
                    skippedFiles++;
                    return;
                }

                if (anyOutput)
                    sb.AppendLine();
                anyOutput = true;

                int remaining = limit - matchCount;
                if (cappedCount <= remaining)
                {
                    sb.Append(fileSb);
                    matchCount += cappedCount;
                }
                else
                {
                    sb.AppendLine(relPath);
                    int written = 0;
                    for (int i = 0; i < cappedCount; i++)
                    {
                        if (written >= remaining) break;
                        var match = fileMatches[i];
                        var lineNum = match.LineNumber.ToString().PadLeft(width);
                        sb.AppendLine($"  {lineNum}: {match.Line}");
                        written++;
                    }
                    matchCount += written;
                }
            }
        });

        producer.GetAwaiter().GetResult();

        if (!anyOutput)
            return "No matches found.";

        if (skippedFiles > 0)
            sb.AppendLine($"\n... and {skippedFiles} more files matched (showing first {maxFiles} files)");

        if (matchCount >= limit)
            sb.AppendLine($"\n... results truncated at {limit} matches. Use a more specific pattern to narrow results.");

        return sb.ToString();
    }

    private static string RunContentSearchWithContext(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, FilePathMatcher[]? allOfMatchers, WildcardPattern[]? excludePathPatterns, int limit,
        int beforeContext, int afterContext,
        int maxFiles, int maxMatchesPerFile,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int matchCount = 0;
        int fileCount = 0;
        int skippedFiles = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = StartFileProducer(fileChannel, pattern, baseDir, globOptions,
            excludePathPatterns, index, excludePathStrings, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var contextLines = matcher.ScanWithContext(beforeContext, afterContext, file);
            if (contextLines.Count == 0) return;
            if (!PassesAllOfCheck(file, allOfMatchers)) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            int width = contextLines[^1].LineNumber.ToString().Length;
            int fileMatchCount = 0;
            foreach (var cl in contextLines)
                if (cl.IsMatch) fileMatchCount++;

            int cappedMatchCount = Math.Min(fileMatchCount, maxMatchesPerFile);

            var fileSb = new StringBuilder();
            fileSb.AppendLine(relPath);

            int matchesEmitted = 0;
            for (int i = 0; i < contextLines.Count; i++)
            {
                if (matchesEmitted >= cappedMatchCount && contextLines[i].IsMatch) break;
                if (matchesEmitted >= cappedMatchCount && !contextLines[i].IsMatch) continue;

                if (i > 0 && contextLines[i].LineNumber != contextLines[i - 1].LineNumber + 1)
                    fileSb.AppendLine($"  {"--".PadLeft(width)}");

                var cl = contextLines[i];
                var lineNum = cl.LineNumber.ToString().PadLeft(width);
                var separator = cl.IsMatch ? ":" : "-";
                fileSb.AppendLine($"  {lineNum}{separator} {cl.Line}");

                if (cl.IsMatch) matchesEmitted++;
            }

            if (fileMatchCount > maxMatchesPerFile)
                fileSb.AppendLine($"  ... and {fileMatchCount - maxMatchesPerFile} more matches in this file");

            lock (outputLock)
            {
                if (matchCount >= limit) return;

                fileCount++;
                if (fileCount > maxFiles)
                {
                    skippedFiles++;
                    return;
                }

                if (anyOutput)
                    sb.AppendLine();
                anyOutput = true;

                int remaining = limit - matchCount;
                if (cappedMatchCount <= remaining)
                {
                    sb.Append(fileSb);
                    matchCount += cappedMatchCount;
                }
                else
                {
                    sb.AppendLine(relPath);
                    int matchesWritten = 0;
                    for (int i = 0; i < contextLines.Count; i++)
                    {
                        if (matchesWritten >= remaining) break;

                        if (i > 0 && contextLines[i].LineNumber != contextLines[i - 1].LineNumber + 1)
                            sb.AppendLine($"  {"--".PadLeft(width)}");

                        var cl = contextLines[i];
                        var lineNum = cl.LineNumber.ToString().PadLeft(width);
                        var separator = cl.IsMatch ? ":" : "-";
                        sb.AppendLine($"  {lineNum}{separator} {cl.Line}");

                        if (cl.IsMatch) matchesWritten++;
                    }
                    matchCount += matchesWritten;
                }
            }
        });

        producer.GetAwaiter().GetResult();

        if (!anyOutput)
            return "No matches found.";

        if (skippedFiles > 0)
            sb.AppendLine($"\n... and {skippedFiles} more files matched (showing first {maxFiles} files)");

        if (matchCount >= limit)
            sb.AppendLine($"\n... results truncated at {limit} matches. Use a more specific pattern to narrow results.");

        return sb.ToString();
    }

    private static string RunCountSearch(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, FilePathMatcher[]? allOfMatchers, WildcardPattern[]? excludePathPatterns, int limit, bool summaryOnly,
        int maxFiles,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int totalMatches = 0;
        int totalFiles = 0;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = StartFileProducer(fileChannel, pattern, baseDir, globOptions,
            excludePathPatterns, index, excludePathStrings, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var fileMatches = matcher.Scan(file);
            if (fileMatches.Count == 0) return;
            if (!PassesAllOfCheck(file, allOfMatchers)) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');

            lock (outputLock)
            {
                totalMatches += fileMatches.Count;
                totalFiles++;
                if (!summaryOnly && totalFiles <= Math.Min(limit, maxFiles))
                    sb.AppendLine($"{relPath}:{fileMatches.Count}");
            }
        });

        producer.GetAwaiter().GetResult();

        if (totalMatches == 0)
            return "No matches found.";

        int shown = Math.Min(limit, maxFiles);
        if (!summaryOnly && totalFiles > shown)
            sb.AppendLine($"... and {totalFiles - shown} more files ({totalFiles} total, showing first {shown})");

        if (!summaryOnly)
            sb.AppendLine();
        sb.AppendLine($"{totalMatches} match{(totalMatches > 1 ? "es" : "")} in {totalFiles} file{(totalFiles > 1 ? "s" : "")}.");

        return sb.ToString();
    }

    /// <summary>
    /// File reader mode: reads the first N lines from each file matching the glob.
    /// No content matching — acts as cross-platform cat/head.
    /// </summary>
    private static string RunReadLines(string pattern, string baseDir, GlobOptions globOptions,
        WildcardPattern[]? excludePathPatterns, int readLines, int maxFiles,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int fileCount = 0;
        int skippedFiles = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = StartFileProducer(fileChannel, pattern, baseDir, globOptions,
            excludePathPatterns, index, excludePathStrings, cancellationToken);

        // Sequential to preserve deterministic output
        foreach (var file in fileChannel.Reader.ReadAllAsync(cancellationToken).ToBlockingEnumerable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (index is null && IsPathExcluded(relPath, excludePathPatterns)) continue;

            fileCount++;
            if (fileCount > maxFiles)
            {
                skippedFiles++;
                continue;
            }

            if (anyOutput)
                sb.AppendLine();
            anyOutput = true;

            sb.AppendLine($"=== {relPath} ===");
            try
            {
                using var reader = new StreamReader(file);
                int lineNum = 0;
                int width = readLines.ToString().Length;
                while (lineNum < readLines && reader.ReadLine() is { } line)
                {
                    lineNum++;
                    sb.Append(lineNum.ToString().PadLeft(width));
                    sb.Append(": ");
                    sb.AppendLine(line);
                }
            }
            catch (IOException) { sb.AppendLine("  (could not read file)"); }
        }

        producer.GetAwaiter().GetResult();

        if (!anyOutput)
            return "No files found.";

        if (skippedFiles > 0)
            sb.AppendLine($"\n... and {skippedFiles} more files ({fileCount} total, showing first {maxFiles})");

        return sb.ToString();
    }

    /// <summary>
    /// read_lines mode with content patterns: finds matches, then reads N total lines
    /// per file centered around match clusters (expanded context).
    /// </summary>
    private static string RunReadLinesWithMatches(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, FilePathMatcher[]? allOfMatchers, WildcardPattern[]? excludePathPatterns,
        int readLines, int maxFiles, int maxMatchesPerFile,
        WorkspaceIndex? index, string[]? excludePathStrings,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int fileCount = 0;
        int skippedFiles = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = StartFileProducer(fileChannel, pattern, baseDir, globOptions,
            excludePathPatterns, index, excludePathStrings, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var fileMatches = matcher.Scan(file);
            if (fileMatches.Count == 0) return;
            if (!PassesAllOfCheck(file, allOfMatchers)) return;

            // Compute context to distribute readLines around matches
            int matchesUsed = Math.Min(fileMatches.Count, maxMatchesPerFile);
            int linesPerMatch = matchesUsed > 0 ? Math.Max(1, readLines / matchesUsed) : readLines;
            int before = linesPerMatch / 2;
            int after = linesPerMatch - before - 1;

            var contextLines = matcher.ScanWithContext(before, after, file);
            if (contextLines.Count == 0) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            int width = contextLines[^1].LineNumber.ToString().Length;
            int matchesEmitted = 0;

            var fileSb = new StringBuilder();
            fileSb.AppendLine(relPath);

            int prevLineNum = -1;
            for (int i = 0; i < contextLines.Count; i++)
            {
                if (matchesEmitted >= matchesUsed && contextLines[i].IsMatch) break;
                if (matchesEmitted >= matchesUsed && !contextLines[i].IsMatch) continue;

                if (prevLineNum >= 0 && contextLines[i].LineNumber != prevLineNum + 1)
                    fileSb.AppendLine($"  {"--".PadLeft(width)}");

                var cl = contextLines[i];
                var lineNum = cl.LineNumber.ToString().PadLeft(width);
                var separator = cl.IsMatch ? ":" : "-";
                fileSb.AppendLine($"  {lineNum}{separator} {cl.Line}");
                prevLineNum = cl.LineNumber;

                if (cl.IsMatch) matchesEmitted++;
            }

            if (fileMatches.Count > maxMatchesPerFile)
                fileSb.AppendLine($"  ... and {fileMatches.Count - maxMatchesPerFile} more matches in this file");

            lock (outputLock)
            {
                fileCount++;
                if (fileCount > maxFiles)
                {
                    skippedFiles++;
                    return;
                }

                if (anyOutput)
                    sb.AppendLine();
                anyOutput = true;
                sb.Append(fileSb);
            }
        });

        producer.GetAwaiter().GetResult();

        if (!anyOutput)
            return "No matches found.";

        if (skippedFiles > 0)
            sb.AppendLine($"\n... and {skippedFiles} more files matched (showing first {maxFiles} files)");

        return sb.ToString();
    }

    /// <summary>
    /// Checks AND mode: file must match the unified matcher AND all individual per-pattern matchers.
    /// Used by modes that call matcher.ContainsMatch themselves (files_only).
    /// </summary>
    private static bool PassesAllOfFilter(string file, FilePathMatcher matcher, FilePathMatcher[]? allOfMatchers)
    {
        if (allOfMatchers is not null)
        {
            foreach (var m in allOfMatchers)
                if (!m.ContainsMatch(file)) return false;
            return true;
        }
        return matcher.ContainsMatch(file);
    }

    /// <summary>
    /// Checks AND mode after an initial OR scan already found hits.
    /// In OR mode (allOfMatchers is null), always returns true since the OR scan already passed.
    /// In AND mode, verifies every pattern has at least one match in the file.
    /// </summary>
    private static bool PassesAllOfCheck(string file, FilePathMatcher[]? allOfMatchers)
    {
        if (allOfMatchers is null) return true;
        foreach (var m in allOfMatchers)
            if (!m.ContainsMatch(file)) return false;
        return true;
    }

    private static string NormalizeContentPattern(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\') { i++; continue; }
            if (pattern[i] is '*' or '?' or '[') return pattern;
        }
        return $"*{pattern}*";
    }

    private static bool IsPathExcluded(string relPath, WildcardPattern[]? excludePathPatterns)
    {
        if (excludePathPatterns is null) return false;
        foreach (var p in excludePathPatterns)
            if (p.IsMatch(relPath)) return true;
        return false;
    }
}
