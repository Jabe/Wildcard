using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Server;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class GlobTool
{
    [McpServerTool(Name = "wildcard_glob"), Description("Like 'find' but doesn't hate you. Find files by glob pattern — blazing fast, respects .gitignore, supports count mode and tree output. Use this instead of shelling out to find/ls. Supports ** recursive, * wildcard, ? single char, [abc] classes, {a,b,c} brace expansion.")]
    public static async Task<string> Glob(
        [Description("Glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\", \"**/*.{cs,razor,css}\")")] string pattern,
        [Description("Base directory to search in (defaults to the first workspace root)")] string? base_directory = null,
        [Description("Glob patterns to exclude file paths (e.g. \"**/node_modules/**\")")] string[]? exclude_paths = null,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        [Description("Follow symbolic links (default: false)")] bool follow_symlinks = false,
        [Description("Maximum number of results to return (default: 10000)")] int limit = 10000,
        [Description("Return only the count of matching files, not the file paths (default: false)")] bool count = false,
        [Description("Render results as an indented ASCII tree instead of a flat list (default: false)")] bool tree = false,
        [Description("Maximum directory depth for tree output (default: 5). Only used when tree=true.")] int max_depth = 5,
        RootsProvider rootsProvider = null!,
        McpServer server = null!,
        WorkspaceIndexManager? indexManager = null,
        CancellationToken cancellationToken = default)
    {
        await rootsProvider.EnsureInitializedAsync(server, cancellationToken);
        var baseDir = rootsProvider.Resolve(base_directory);
        var index = indexManager is not null ? await indexManager.GetIndexAsync(baseDir) : null;

        // Use index when available and options match indexed state
        if (index is not null && respect_gitignore && !follow_symlinks)
        {
            var paths = new List<string>();
            int matched = 0;

            foreach (var file in index.MatchGlob(pattern, baseDir, exclude_paths))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matched++;
                if (!count && matched <= limit)
                    paths.Add(Path.GetRelativePath(baseDir, file).Replace('\\', '/'));
            }

            if (matched == 0)
                return "No files found.";
            if (count)
                return $"{matched} file{(matched > 1 ? "s" : "")} found.";
            if (tree)
            {
                var treeOutput = TreeRenderer.Render(paths, max_depth);
                if (matched > limit)
                    return treeOutput + $"\n... and {matched - limit} more files ({matched} total, showing first {limit})";
                return treeOutput;
            }
            var sb = new StringBuilder();
            foreach (var p in paths)
                sb.AppendLine(p);
            if (matched > limit)
                sb.AppendLine($"\n... and {matched - limit} more files ({matched} total, showing first {limit})");
            else
                sb.AppendLine($"\n{matched} files found.");

            return sb.ToString();
        }

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

        var sb2 = new StringBuilder();
        var paths2 = tree ? new List<string>() : null;
        int matched2 = 0;

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

            matched2++;
            if (!count && matched2 <= limit)
            {
                if (tree)
                    paths2!.Add(relPath);
                else
                    sb2.AppendLine(relPath);
            }
        }

        await producer;

        if (matched2 == 0)
            return "No files found.";

        if (count)
            return $"{matched2} file{(matched2 > 1 ? "s" : "")} found.";

        if (tree)
        {
            var treeOutput = TreeRenderer.Render(paths2!, max_depth);
            if (matched2 > limit)
                return treeOutput + $"\n... and {matched2 - limit} more files ({matched2} total, showing first {limit})";
            return treeOutput;
        }

        if (matched2 > limit)
            sb2.AppendLine($"\n... and {matched2 - limit} more files ({matched2} total, showing first {limit})");
        else
            sb2.AppendLine($"\n{matched2} files found.");

        return sb2.ToString();
    }
}
