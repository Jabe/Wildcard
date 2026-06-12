using System.Text;

namespace Wildcard;

/// <summary>
/// Find-and-replace engine for files on disk.
/// Supports literal replacement and capture-group replacement via wildcard patterns.
/// Find text containing newlines is always matched literally (wildcards cannot span lines);
/// its line endings are normalized to each file's own style before matching.
/// </summary>
public static class FileReplacer
{
    /// <summary>A single line replacement within a file.</summary>
    public readonly record struct LineReplacement(int LineNumber, string OriginalLine, string ReplacedLine);

    /// <summary>
    /// Replacement results for a single file.
    /// <paramref name="NormalizedLineEnding"/> is non-null when the find/replace text's line
    /// endings were converted to match the file's style; it holds the file's line ending.
    /// </summary>
    public readonly record struct FileResult(string FilePath, List<LineReplacement> Replacements, string? Error = null, string? NormalizedLineEnding = null);

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

    /// <summary>
    /// Returns true if the file contains the literal find text at least once.
    /// Multi-line find text is matched against the whole file content with
    /// line endings normalized to the file's own style.
    /// </summary>
    public static bool ContainsLiteralMatch(string filePath, string find, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(find);
        if (find.Length == 0) return false;

        if (!CanProcessFile(filePath))
            return false;

        var (content, _) = ReadFileRaw(filePath);
        if (content is null)
            return false;

        var lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";
        var normalizedFind = NormalizeLineEndings(find, lineEnding);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return content.Contains(normalizedFind, comparison);
    }

    internal static FileResult ComputeReplacements(string filePath, string find, string replace, bool ignoreCase)
    {
        if (find.Contains('\n'))
            return ReplaceMultiLine(filePath, find, replace, ignoreCase, write: false);

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
        if (find.Contains('\n'))
            return ReplaceMultiLine(filePath, find, replace, ignoreCase, write: true);

        var empty = new FileResult(filePath, []);

        if (!CanProcessFile(filePath))
            return empty;

        var (lines, encoding, lineEnding) = ReadFile(filePath);
        if (lines is null)
            return empty;

        // Single pass: compute replacements and apply to the same lines array
        bool isLiteral = IsLiteralPattern(find);
        var replacements = new List<LineReplacement>();
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        WildcardPattern? pattern = isLiteral ? null : WildcardPattern.Compile(find, ignoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string newLine;
            if (isLiteral)
            {
                newLine = lines[i].Replace(find, replace, comparison);
            }
            else if (pattern!.TryMatch(lines[i], out var captures))
            {
                newLine = BuildCaptureReplacement(replace, captures);
            }
            else
            {
                continue;
            }

            if (newLine != lines[i])
            {
                replacements.Add(new LineReplacement(i + 1, lines[i], newLine));
                lines[i] = newLine;
            }
        }

        if (replacements.Count == 0)
            return empty;

        var newContent = string.Join(lineEnding, lines);
        WriteFileAtomic(filePath, newContent, encoding);

        return new FileResult(filePath, replacements);
    }

    /// <summary>
    /// Literal whole-content replacement for find text spanning multiple lines.
    /// Each replacement's OriginalLine/ReplacedLine hold the full (multi-line) matched
    /// and replacement text; LineNumber is the 1-based line where the match starts.
    /// </summary>
    private static FileResult ReplaceMultiLine(string filePath, string find, string replace, bool ignoreCase, bool write)
    {
        var empty = new FileResult(filePath, []);

        if (!CanProcessFile(filePath))
            return empty;

        var (content, encoding) = ReadFileRaw(filePath);
        if (content is null)
            return empty;

        var lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";
        var normalizedFind = NormalizeLineEndings(find, lineEnding);
        var normalizedReplace = NormalizeLineEndings(replace, lineEnding);
        string? normalizedLineEnding = normalizedFind != find || normalizedReplace != replace ? lineEnding : null;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var replacements = new List<LineReplacement>();
        var sb = write ? new StringBuilder(content.Length) : null;

        int pos = 0;
        while (pos < content.Length)
        {
            int idx = content.IndexOf(normalizedFind, pos, comparison);
            if (idx < 0) break;

            var original = content.Substring(idx, normalizedFind.Length);
            if (original != normalizedReplace)
            {
                int lineNumber = content.AsSpan(0, idx).Count('\n') + 1;
                replacements.Add(new LineReplacement(lineNumber, original, normalizedReplace));
            }

            sb?.Append(content, pos, idx - pos).Append(normalizedReplace);
            pos = idx + normalizedFind.Length;
        }

        if (replacements.Count == 0)
            return empty;

        if (write)
        {
            sb!.Append(content, pos, content.Length - pos);
            WriteFileAtomic(filePath, sb.ToString(), encoding);
        }

        return new FileResult(filePath, replacements, NormalizedLineEnding: normalizedLineEnding);
    }

    private static string NormalizeLineEndings(string text, string lineEnding)
    {
        var unified = text.Replace("\r\n", "\n");
        return lineEnding == "\n" ? unified : unified.Replace("\n", lineEnding);
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
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    private static (string[]? Lines, Encoding Encoding, string LineEnding) ReadFile(string filePath)
    {
        var (content, encoding) = ReadFileRaw(filePath);
        if (content is null)
            return (null, encoding, "\n");

        // Detect line ending style
        string lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";

        // Split into lines
        var lines = content.Split(lineEnding);

        return (lines, encoding, lineEnding);
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static (string? Content, Encoding Encoding) ReadFileRaw(string filePath)
    {
        try
        {
            // Default to BOM-less UTF-8 so writing back doesn't add a BOM the file
            // never had; BOM detection switches CurrentEncoding when one is present.
            using var reader = new StreamReader(filePath, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            return (content, reader.CurrentEncoding);
        }
        catch (IOException)
        {
            return (null, Utf8NoBom);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, Utf8NoBom);
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
            try { File.Delete(tempPath); } catch (IOException) { }
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
