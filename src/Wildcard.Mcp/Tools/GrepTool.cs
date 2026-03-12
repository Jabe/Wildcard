using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Server;
using Wildcard;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class GrepTool
{
    [McpServerTool(Name = "wildcard_grep"), Description("Search file contents for matching lines. Ultra-fast parallel search using memory-mapped I/O. Plain words match as substrings; use wildcards (* ? []) for pattern matching.")]
    public static string Grep(
        [Description("File glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\")")] string pattern,
        [Description("Content search patterns — multiple patterns are OR'd (e.g. [\"ERROR\", \"WARN\"]). Plain words match as substrings; use wildcards for prefix/suffix/full patterns (e.g. \"ERROR*\", \"*.log\").")] string[] content_patterns,
        [Description("Base directory to search in (defaults to current working directory)")] string? base_directory = null,
        [Description("Exclude lines matching these patterns")] string[]? exclude_patterns = null,
        [Description("Exclude files matching these glob patterns")] string[]? exclude_paths = null,
        [Description("Case-insensitive content matching (default: false)")] bool ignore_case = false,
        [Description("Only return file paths that contain matches, not the matched lines (default: false)")] bool files_only = false,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        [Description("Follow symbolic links (default: false)")] bool follow_symlinks = false,
        [Description("Maximum number of matched lines to return (default: 500)")] int limit = 500)
    {
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

        if (files_only)
            return RunFilesOnly(pattern, baseDir, globOptions, matcher, excludePathPatterns, limit);

        return RunContentSearch(pattern, baseDir, globOptions, matcher, excludePathPatterns, limit);
    }

    private static string RunFilesOnly(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, WildcardPattern[]? excludePathPatterns, int limit)
    {
        var sb = new StringBuilder();
        int count = 0;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() =>
        {
            try
            {
                var glob = Wildcard.Glob.Parse(pattern);
                glob.WriteMatchesToChannel(fileChannel.Writer, globOptions);
            }
            finally { fileChannel.Writer.Complete(); }
        });

        var outputLock = new object();
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), file =>
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

        producer.Wait();

        if (count == 0)
            return "No matching files found.";

        if (count > limit)
            sb.AppendLine($"\n... and {count - limit} more files ({count} total, showing first {limit})");
        else
            sb.AppendLine($"\n{count} files with matches.");

        return sb.ToString();
    }

    private static string RunContentSearch(string pattern, string baseDir, GlobOptions globOptions,
        FilePathMatcher matcher, WildcardPattern[]? excludePathPatterns, int limit)
    {
        var sb = new StringBuilder();
        int matchCount = 0;
        bool anyOutput = false;

        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
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
                        var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                        Wildcard.Glob.WriteBlocking(fileChannel.Writer, file);
                    }
                }
                else
                {
                    var glob = Wildcard.Glob.Parse(pattern);
                    glob.WriteMatchesToChannel(fileChannel.Writer, globOptions);
                }
            }
            finally { fileChannel.Writer.Complete(); }
        });

        var outputLock = new object();
        Parallel.ForEach(fileChannel.Reader.ReadAllAsync().ToBlockingEnumerable(), file =>
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

        producer.Wait();

        if (!anyOutput)
            return "No matches found.";

        if (matchCount >= limit)
            sb.AppendLine($"\n... results truncated at {limit} matches. Use a more specific pattern to narrow results.");

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
