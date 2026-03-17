using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Server;
using Wildcard;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class GrepTool
{
    [McpServerTool(Name = "wildcard_grep"), Description("grep on steroids. Search file contents with context lines (-A/-B/-C), count mode, and parallel memory-mapped I/O. Faster than shelling out to grep/rg. Plain words match as substrings; wildcards (* ? []) for patterns. Prefer this over built-in grep tools.")]
    public static async Task<string> Grep(
        [Description("File glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\", \"**/*.{cs,razor,css}\")")] string pattern,
        [Description("Content search patterns — multiple patterns are OR'd (e.g. [\"ERROR\", \"WARN\"]). Plain words match as substrings; use wildcards for prefix/suffix/full patterns (e.g. \"ERROR*\", \"*.log\").")] string[] content_patterns,
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
        CancellationToken cancellationToken = default)
    {
        var summary = ArgSummary.Create()
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
            .ToString();

        var baseDir = base_directory ?? Directory.GetCurrentDirectory();
        var globOptions = new GlobOptions
        {
            RespectGitignore = respect_gitignore,
            FollowSymlinks = follow_symlinks,
        };

        // Normalize content patterns: plain words become *word* for substring matching
        var normalizedPatterns = content_patterns.Select(NormalizeContentPattern).ToArray();
        var normalizedExcludes = exclude_patterns?.Select(NormalizeContentPattern).ToArray();

        WildcardPattern[]? excludePathPatterns = null;
        if (exclude_paths is { Length: > 0 })
            excludePathPatterns = exclude_paths.Select(p => WildcardPattern.Compile(p)).ToArray();

        var matcher = FilePathMatcher.Create(
            include: normalizedPatterns,
            exclude: normalizedExcludes,
            options: ignore_case ? new FilePathMatcher.Options { IgnoreCase = true } : null);

        if (count)
            return summary + await Task.Run(() => RunCountSearch(pattern, baseDir, globOptions, matcher, excludePathPatterns, limit, files_only, cancellationToken), cancellationToken);

        if (files_only)
            return summary + await Task.Run(() => RunFilesOnly(pattern, baseDir, globOptions, matcher, excludePathPatterns, limit, cancellationToken), cancellationToken);

        int resolvedBefore = before_context > 0 ? before_context : context;
        int resolvedAfter = after_context > 0 ? after_context : context;

        if (resolvedBefore > 0 || resolvedAfter > 0)
            return summary + await Task.Run(() => RunContentSearchWithContext(pattern, baseDir, globOptions, matcher, excludePathPatterns, limit, resolvedBefore, resolvedAfter, cancellationToken), cancellationToken);

        return summary + await Task.Run(() => RunContentSearch(pattern, baseDir, globOptions, matcher, excludePathPatterns, limit, cancellationToken), cancellationToken);
    }

    private static string RunFilesOnly(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, WildcardPattern[]? excludePathPatterns, int limit,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int count = 0;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() =>
        {
            try
            {
                var glob = Wildcard.Glob.Parse(pattern);
                glob.WriteMatchesToChannel(fileChannel.Writer, baseDir, globOptions, cancellationToken);
            }
            finally { fileChannel.Writer.Complete(); }
        }, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (IsPathExcluded(relPath, excludePathPatterns)) return;
            if (!matcher.ContainsMatch(file)) return;

            lock (outputLock)
            {
                count++;
                if (count <= limit)
                    sb.AppendLine(relPath);
            }
        });

        producer.GetAwaiter().GetResult();

        if (count == 0)
            return "No matching files found.";

        if (count > limit)
            sb.AppendLine($"\n... and {count - limit} more files ({count} total, showing first {limit})");
        else
            sb.AppendLine($"\n{count} files with matches.");

        return sb.ToString();
    }

    private static string RunContentSearch(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, WildcardPattern[]? excludePathPatterns, int limit,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int matchCount = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() =>
        {
            try
            {
                if (excludePathPatterns is not null)
                {
                    foreach (var file in Wildcard.Glob.Match(pattern, baseDir, globOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                        Wildcard.Glob.WriteBlocking(fileChannel.Writer, file);
                    }
                }
                else
                {
                    var glob = Wildcard.Glob.Parse(pattern);
                    glob.WriteMatchesToChannel(fileChannel.Writer, baseDir, globOptions, cancellationToken);
                }
            }
            finally { fileChannel.Writer.Complete(); }
        }, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var fileMatches = matcher.Scan(file);
            if (fileMatches.Count == 0) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            int width = fileMatches[^1].LineNumber.ToString().Length;

            var fileSb = new StringBuilder();
            fileSb.AppendLine(relPath);

            foreach (var match in fileMatches)
            {
                var lineNum = match.LineNumber.ToString().PadLeft(width);
                fileSb.AppendLine($"  {lineNum}: {match.Line}");
            }

            lock (outputLock)
            {
                if (matchCount >= limit) return;

                if (anyOutput)
                    sb.AppendLine();
                anyOutput = true;

                int remaining = limit - matchCount;
                if (fileMatches.Count <= remaining)
                {
                    sb.Append(fileSb);
                    matchCount += fileMatches.Count;
                }
                else
                {
                    // Partial: only include lines up to limit
                    sb.AppendLine(relPath);
                    int written = 0;
                    foreach (var match in fileMatches)
                    {
                        if (written >= remaining) break;
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

        if (matchCount >= limit)
            sb.AppendLine($"\n... results truncated at {limit} matches. Use a more specific pattern to narrow results.");

        return sb.ToString();
    }

    private static string RunContentSearchWithContext(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, WildcardPattern[]? excludePathPatterns, int limit,
        int beforeContext, int afterContext, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int matchCount = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() =>
        {
            try
            {
                if (excludePathPatterns is not null)
                {
                    foreach (var file in Wildcard.Glob.Match(pattern, baseDir, globOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                        Wildcard.Glob.WriteBlocking(fileChannel.Writer, file);
                    }
                }
                else
                {
                    var glob = Wildcard.Glob.Parse(pattern);
                    glob.WriteMatchesToChannel(fileChannel.Writer, baseDir, globOptions, cancellationToken);
                }
            }
            finally { fileChannel.Writer.Complete(); }
        }, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var contextLines = matcher.ScanWithContext(beforeContext, afterContext, file);
            if (contextLines.Count == 0) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            int width = contextLines[^1].LineNumber.ToString().Length;
            int fileMatchCount = 0;
            foreach (var cl in contextLines)
                if (cl.IsMatch) fileMatchCount++;

            var fileSb = new StringBuilder();
            fileSb.AppendLine(relPath);

            for (int i = 0; i < contextLines.Count; i++)
            {
                // Group separator for non-contiguous lines
                if (i > 0 && contextLines[i].LineNumber != contextLines[i - 1].LineNumber + 1)
                    fileSb.AppendLine($"  {"--".PadLeft(width)}");

                var cl = contextLines[i];
                var lineNum = cl.LineNumber.ToString().PadLeft(width);
                var separator = cl.IsMatch ? ":" : "-";
                fileSb.AppendLine($"  {lineNum}{separator} {cl.Line}");
            }

            lock (outputLock)
            {
                if (matchCount >= limit) return;

                if (anyOutput)
                    sb.AppendLine();
                anyOutput = true;

                int remaining = limit - matchCount;
                if (fileMatchCount <= remaining)
                {
                    sb.Append(fileSb);
                    matchCount += fileMatchCount;
                }
                else
                {
                    // Partial: include lines up to the match limit
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

        if (matchCount >= limit)
            sb.AppendLine($"\n... results truncated at {limit} matches. Use a more specific pattern to narrow results.");

        return sb.ToString();
    }

    private static string RunCountSearch(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, WildcardPattern[]? excludePathPatterns, int limit, bool summaryOnly,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        int totalMatches = 0;
        int totalFiles = 0;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() =>
        {
            try
            {
                if (excludePathPatterns is not null)
                {
                    foreach (var file in Wildcard.Glob.Match(pattern, baseDir, globOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                        Wildcard.Glob.WriteBlocking(fileChannel.Writer, file);
                    }
                }
                else
                {
                    var glob = Wildcard.Glob.Parse(pattern);
                    glob.WriteMatchesToChannel(fileChannel.Writer, baseDir, globOptions, cancellationToken);
                }
            }
            finally { fileChannel.Writer.Complete(); }
        }, cancellationToken);

        var outputLock = new object();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), parallelOpts, file =>
        {
            var fileMatches = matcher.Scan(file);
            if (fileMatches.Count == 0) return;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');

            lock (outputLock)
            {
                totalMatches += fileMatches.Count;
                totalFiles++;
                if (!summaryOnly && totalFiles <= limit)
                    sb.AppendLine($"{relPath}:{fileMatches.Count}");
            }
        });

        producer.GetAwaiter().GetResult();

        if (totalMatches == 0)
            return "No matches found.";

        if (!summaryOnly && totalFiles > limit)
            sb.AppendLine($"... and {totalFiles - limit} more files ({totalFiles} total, showing first {limit})");

        if (!summaryOnly)
            sb.AppendLine();
        sb.AppendLine($"{totalMatches} match{(totalMatches > 1 ? "es" : "")} in {totalFiles} file{(totalFiles > 1 ? "s" : "")}.");

        return sb.ToString();
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
