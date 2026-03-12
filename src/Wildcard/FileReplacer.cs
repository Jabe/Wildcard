using System.Text;

namespace Wildcard;

/// <summary>
/// Find-and-replace engine for files on disk.
/// Supports literal replacement and capture-group replacement via wildcard patterns.
/// </summary>
public static class FileReplacer
{
    /// <summary>A single line replacement within a file.</summary>
    public readonly record struct LineReplacement(int LineNumber, string OriginalLine, string ReplacedLine);

    /// <summary>Replacement results for a single file.</summary>
    public readonly record struct FileResult(string FilePath, List<LineReplacement> Replacements, string? Error = null);

    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private const int BinaryCheckBytes = 8192;

    /// <summary>
    /// Compute replacements without writing to disk.
    /// </summary>
    public static List<FileResult> Preview(string[] filePaths, string find, string replace, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(replace);
        if (find.Length == 0) throw new ArgumentException("Find string cannot be empty.", nameof(find));

        if (filePaths.Length == 0) return [];

        var results = new List<FileResult>(filePaths.Length);
        var lockObj = new object();

        Parallel.For(0, filePaths.Length, i =>
        {
            var result = ComputeReplacements(filePaths[i], find, replace, ignoreCase);
            if (result.Replacements.Count > 0)
            {
                lock (lockObj)
                    results.Add(result);
            }
        });

        return results;
    }

    /// <summary>
    /// Compute and apply replacements, writing changes to disk atomically.
    /// </summary>
    public static List<FileResult> Apply(string[] filePaths, string find, string replace, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(replace);
        if (find.Length == 0) throw new ArgumentException("Find string cannot be empty.", nameof(find));

        if (filePaths.Length == 0) return [];

        var results = new List<FileResult>(filePaths.Length);
        var lockObj = new object();

        Parallel.For(0, filePaths.Length, i =>
        {
            FileResult result;
            try
            {
                result = ApplyToFile(filePaths[i], find, replace, ignoreCase);
            }
            catch (Exception ex)
            {
                result = new FileResult(filePaths[i], [], Error: ex.Message);
            }

            if (result.Replacements.Count > 0 || result.Error is not null)
            {
                lock (lockObj)
                    results.Add(result);
            }
        });

        return results;
    }

    internal static FileResult ComputeReplacements(string filePath, string find, string replace, bool ignoreCase)
    {
        var empty = new FileResult(filePath, []);

        if (!CanProcessFile(filePath))
            return empty;

        var (lines, _, _) = ReadFile(filePath);
        if (lines is null)
            return empty;

        bool isLiteral = IsLiteralPattern(find);
        var replacements = new List<LineReplacement>();

        if (isLiteral)
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (int i = 0; i < lines.Length; i++)
            {
                var newLine = lines[i].Replace(find, replace, comparison);
                if (newLine != lines[i])
                    replacements.Add(new LineReplacement(i + 1, lines[i], newLine));
            }
        }
        else
        {
            var pattern = WildcardPattern.Compile(find, ignoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                if (pattern.TryMatch(lines[i], out var captures))
                {
                    var newLine = BuildCaptureReplacement(replace, captures);
                    if (newLine != lines[i])
                        replacements.Add(new LineReplacement(i + 1, lines[i], newLine));
                }
            }
        }

        return new FileResult(filePath, replacements);
    }

    private static FileResult ApplyToFile(string filePath, string find, string replace, bool ignoreCase)
    {
        var result = ComputeReplacements(filePath, find, replace, ignoreCase);
        if (result.Replacements.Count == 0)
            return result;

        // Re-read and apply
        var (lines, encoding, lineEnding) = ReadFile(filePath);
        if (lines is null)
            return new FileResult(filePath, []);

        bool isLiteral = IsLiteralPattern(find);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        WildcardPattern? pattern = isLiteral ? null : WildcardPattern.Compile(find, ignoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            if (isLiteral)
            {
                lines[i] = lines[i].Replace(find, replace, comparison);
            }
            else if (pattern!.TryMatch(lines[i], out var captures))
            {
                lines[i] = BuildCaptureReplacement(replace, captures);
            }
        }

        var newContent = string.Join(lineEnding, lines);
        WriteFileAtomic(filePath, newContent, encoding);

        return result;
    }

    private static bool CanProcessFile(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            return false;

        if (fileInfo.IsReadOnly)
            return false;

        if (fileInfo.Length > MaxFileSizeBytes)
            return false;

        // Binary file detection: check first bytes for null
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var checkSize = (int)Math.Min(BinaryCheckBytes, fs.Length);
            var buffer = new byte[checkSize];
            var bytesRead = fs.Read(buffer, 0, checkSize);
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                    return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static (string[]? Lines, Encoding Encoding, string LineEnding) ReadFile(string filePath)
    {
        try
        {
            Encoding encoding;
            string content;
            using (var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true))
            {
                content = reader.ReadToEnd();
                encoding = reader.CurrentEncoding;
            }

            // Detect line ending style
            string lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";

            // Split into lines
            var lines = content.Split(lineEnding);

            return (lines, encoding, lineEnding);
        }
        catch
        {
            return (null, Encoding.UTF8, "\n");
        }
    }

    private static void WriteFileAtomic(string filePath, string content, Encoding encoding)
    {
        var tempPath = filePath + ".wildcard-tmp";
        try
        {
            File.WriteAllText(tempPath, content, encoding);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static bool IsLiteralPattern(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\') { i++; continue; }
            if (pattern[i] is '*' or '?' or '[') return false;
        }
        return true;
    }

    public static string BuildCaptureReplacement(string template, string[] captures)
    {
        var sb = new StringBuilder(template.Length + 64);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '$' && i + 1 < template.Length && char.IsDigit(template[i + 1]))
            {
                // Parse the digit(s) after $
                int start = i + 1;
                int end = start;
                while (end < template.Length && char.IsDigit(template[end]))
                    end++;

                int captureIdx = int.Parse(template.AsSpan(start, end - start)) - 1; // $1 = captures[0]
                if (captureIdx >= 0 && captureIdx < captures.Length)
                    sb.Append(captures[captureIdx]);
                else
                    sb.Append(template.AsSpan(i, end - i)); // leave as-is if out of range

                i = end - 1; // loop will increment
            }
            else
            {
                sb.Append(template[i]);
            }
        }
        return sb.ToString();
    }
}
