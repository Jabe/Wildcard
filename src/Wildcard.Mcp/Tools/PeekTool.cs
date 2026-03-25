using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class PeekTool
{
    [McpServerTool(Name = "wildcard_peek"), Description("Batch file reader. Pass a list of file paths with optional line ranges and get all their contents in one response. Respects a character budget so it won't flood context. Use after grep/glob to read the files you identified.")]
    public static string Peek(
        [Description("File paths to read (relative to base_directory, or absolute)")] string[] files,
        [Description("Base directory to resolve relative paths against (defaults to current working directory)")] string? base_directory = null,
        [Description("Optional 1-based start line per file (parallel array with files). Omit or use 0 for 'start of file'.")] int[]? start_lines = null,
        [Description("Optional 1-based end line per file (parallel array with files). Omit or use 0 for 'end of file'.")] int[]? end_lines = null,
        [Description("Total character budget across all files (default: 10000). Once exceeded, remaining files are skipped with a note.")] int max_chars = 10000)
    {
        if (files is null or { Length: 0 })
            return "No files specified.";

        var baseDir = PathGuard.Resolve(base_directory);

        var sb = new StringBuilder();
        int charsUsed = 0;
        int filesRead = 0;

        for (int i = 0; i < files.Length; i++)
        {
            if (charsUsed >= max_chars)
            {
                sb.AppendLine($"\n... budget exceeded ({max_chars} chars). {files.Length - i} file(s) remaining.");
                break;
            }

            var filePath = files[i];
            string absolutePath;
            if (Path.IsPathRooted(filePath))
            {
                absolutePath = Path.GetFullPath(filePath);
            }
            else
            {
                absolutePath = Path.GetFullPath(Path.Combine(baseDir, filePath));
            }

            // Security: verify path is within allowed root
            try
            {
                PathGuard.Resolve(Path.GetDirectoryName(absolutePath));
            }
            catch (UnauthorizedAccessException)
            {
                sb.AppendLine($"\n=== {filePath} ===");
                sb.AppendLine("Access denied: path is outside the allowed root.");
                continue;
            }

            if (!File.Exists(absolutePath))
            {
                sb.AppendLine($"\n=== {filePath} ===");
                sb.AppendLine("File not found.");
                continue;
            }

            int startLine = start_lines is not null && i < start_lines.Length && start_lines[i] > 0 ? start_lines[i] : 1;
            int endLine = end_lines is not null && i < end_lines.Length && end_lines[i] > 0 ? end_lines[i] : int.MaxValue;

            var relPath = Path.GetRelativePath(baseDir, absolutePath).Replace('\\', '/');
            int charsRemaining = max_chars - charsUsed;

            var fileSb = new StringBuilder();
            int lineNum = 0;
            int lastLineRead = 0;
            bool truncated = false;

            try
            {
                using var reader = new StreamReader(absolutePath);
                while (reader.ReadLine() is { } line)
                {
                    lineNum++;
                    if (lineNum < startLine) continue;
                    if (lineNum > endLine) break;

                    // +3 for line number formatting overhead per line
                    int lineChars = line.Length + lineNum.ToString().Length + 4;
                    if (fileSb.Length + lineChars > charsRemaining)
                    {
                        truncated = true;
                        break;
                    }

                    int width = endLine < int.MaxValue ? endLine.ToString().Length : Math.Max(lineNum.ToString().Length, 3);
                    fileSb.Append(lineNum.ToString().PadLeft(width));
                    fileSb.Append(": ");
                    fileSb.AppendLine(line);
                    lastLineRead = lineNum;
                }
            }
            catch (IOException)
            {
                sb.AppendLine($"\n=== {filePath} ===");
                sb.AppendLine("Could not read file.");
                continue;
            }

            if (fileSb.Length == 0 && !truncated)
            {
                sb.AppendLine($"\n=== {relPath} (empty/no lines in range) ===");
                continue;
            }

            string rangeLabel = endLine < int.MaxValue
                ? $"lines {startLine}-{lastLineRead}"
                : lastLineRead > 0 ? $"lines {startLine}-{lastLineRead}" : "empty";

            if (filesRead > 0) sb.AppendLine();
            sb.AppendLine($"=== {relPath} ({rangeLabel}) ===");
            sb.Append(fileSb);

            if (truncated)
                sb.AppendLine($"... truncated (budget limit reached)");

            charsUsed += fileSb.Length;
            filesRead++;
        }

        if (filesRead == 0 && sb.Length == 0)
            return "No files could be read.";

        return sb.ToString();
    }
}
