using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Server;
using Wildcard;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class GlobTool
{
    [McpServerTool(Name = "wildcard_glob"), Description("Like 'find' but doesn't hate you. Find files by glob pattern — blazing fast, respects .gitignore, supports count mode. Use this instead of shelling out to find/ls. Supports ** recursive, * wildcard, ? single char, [abc] classes, {a,b,c} brace expansion.")]
    public static async Task<string> Glob(
        [Description("Glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\", \"**/*.{cs,razor,css}\")")] string pattern,
        [Description("Base directory to search in (defaults to current working directory)")] string? base_directory = null,
        [Description("Glob patterns to exclude file paths (e.g. \"**/node_modules/**\")")] string[]? exclude_paths = null,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        [Description("Follow symbolic links (default: false)")] bool follow_symlinks = false,
        [Description("Maximum number of results to return (default: 10000)")] int limit = 10000,
        [Description("Return only the count of matching files, not the file paths (default: false)")] bool count = false,
        CancellationToken cancellationToken = default)
    {
        var summary = ArgSummary.Create()
            .Arg("pattern", pattern)
            .Arg("base_directory", base_directory)
            .Arg("exclude_paths", exclude_paths)
            .Arg("respect_gitignore", respect_gitignore, true)
            .Arg("follow_symlinks", follow_symlinks, false)
            .Arg("limit", limit, 10000)
            .Arg("count", count, false)
            .ToString();

        var (baseDir, guardError) = PathGuard.Resolve(base_directory);
        if (guardError is not null) return summary + guardError;
        var options = new GlobOptions
        {
            RespectGitignore = respect_gitignore,
            FollowSymlinks = follow_symlinks,
        };

        WildcardPattern[]? excludePatterns = null;
        if (exclude_paths is { Length: > 0 })
            excludePatterns = exclude_paths.Select(p => WildcardPattern.Compile(p)).ToArray();

        var channel = Channel.CreateUnbounded<string>();
        var producer = Task.Run(() =>
        {
            try { Wildcard.Glob.MatchToChannel(pattern, channel.Writer, baseDir, options, cancellationToken); }
            finally { channel.Writer.TryComplete(); }
        }, cancellationToken);

        var sb = new StringBuilder();
        int matched = 0;

        await foreach (var file in channel.Reader.ReadAllAsync(cancellationToken))
        {
            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');

            if (excludePatterns is not null)
            {
                bool excluded = false;
                foreach (var ep in excludePatterns)
                {
                    if (ep.IsMatch(relPath)) { excluded = true; break; }
                }
                if (excluded) continue;
            }

            matched++;
            if (!count && matched <= limit)
                sb.AppendLine(relPath);
        }

        await producer;

        if (matched == 0)
            return summary + "No files found.";

        if (count)
            return summary + $"{matched} file{(matched > 1 ? "s" : "")} found.";

        if (matched > limit)
            sb.AppendLine($"\n... and {matched - limit} more files ({matched} total, showing first {limit})");
        else
            sb.AppendLine($"\n{matched} files found.");

        return summary + sb.ToString();
    }
}
