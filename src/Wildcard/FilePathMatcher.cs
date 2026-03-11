using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Wildcard;

/// <summary>
/// High-performance, cross-platform file content scanner.
/// Scans files on disk for lines matching wildcard patterns using memory-mapped I/O.
/// Supports include/exclude pattern semantics and parallel multi-file scanning.
/// </summary>
public sealed class FilePathMatcher
{
    /// <summary>
    /// A matched line from a scanned file.
    /// </summary>
    public readonly record struct LineMatch(string FilePath, int LineNumber, string Line);

    /// <summary>
    /// Options for file scanning.
    /// </summary>
    public sealed record Options
    {
        /// <summary>Default options: UTF-8 with BOM detection.</summary>
        public static Options Default { get; } = new();

        /// <summary>
        /// File encoding. Null (default) uses UTF-8 with BOM detection.
        /// </summary>
        public Encoding? Encoding { get; init; }

        /// <summary>
        /// If true, pattern matching is case-insensitive.
        /// </summary>
        public bool IgnoreCase { get; init; }
    }

    private readonly WildcardPattern[] _includes;
    private readonly WildcardPattern[]? _excludes;
    private readonly Encoding _encoding;
    private readonly int _minLineLength;
    private readonly bool _ignoreCase;

    // Single-include fast path
    private readonly WildcardPattern? _singleInclude;

    // Byte-level pre-filter for ASCII patterns over UTF-8
    private readonly byte[]? _bytePrefix;
    private readonly byte[]? _byteSuffix;
    private readonly WildcardPattern.PatternShape _bytePreFilterShape;
    private readonly bool _bytePreFilterEnabled;

    private FilePathMatcher(WildcardPattern[] includes, WildcardPattern[]? excludes, Options options)
    {
        _includes = includes;
        _excludes = excludes;
        _encoding = options.Encoding ?? Encoding.UTF8;
        _ignoreCase = options.IgnoreCase;
        _singleInclude = includes.Length == 1 ? includes[0] : null;
        _minLineLength = ComputeMinLength(includes);
        InitBytePreFilter(_singleInclude, _encoding, out _bytePreFilterEnabled, out _bytePreFilterShape, out _bytePrefix, out _byteSuffix);
    }

    private static void InitBytePreFilter(
        WildcardPattern? pattern, Encoding encoding,
        out bool enabled, out WildcardPattern.PatternShape shape,
        out byte[]? bytePrefix, out byte[]? byteSuffix)
    {
        enabled = false;
        shape = default;
        bytePrefix = null;
        byteSuffix = null;

        if (pattern is null || encoding.CodePage != 65001)
            return;

        var s = pattern.Shape;
        if (s == WildcardPattern.PatternShape.General)
            return;

        // Check literals are pure ASCII
        if (pattern.Prefix is not null && !IsAscii(pattern.Prefix))
            return;
        if (pattern.Suffix is not null && !IsAscii(pattern.Suffix))
            return;

        shape = s;
        bytePrefix = pattern.Prefix is not null ? Encoding.UTF8.GetBytes(pattern.Prefix) : null;
        byteSuffix = pattern.Suffix is not null ? Encoding.UTF8.GetBytes(pattern.Suffix) : null;
        enabled = true;
    }

    private static bool IsAscii(string s)
    {
        foreach (char c in s)
        {
            if (c >= 128) return false;
        }
        return true;
    }

    /// <summary>
    /// Creates a matcher with a single include pattern.
    /// </summary>
    public static FilePathMatcher Create(string include, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(include);
        return Create([include], null, options);
    }

    /// <summary>
    /// Creates a matcher with include and optional exclude patterns.
    /// A line matches if it matches ANY include pattern AND does NOT match ANY exclude pattern.
    /// </summary>
    public static FilePathMatcher Create(string[] include, string[]? exclude = null, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(include);
        if (include.Length == 0) throw new ArgumentException("At least one include pattern is required.", nameof(include));

        var opts = options ?? Options.Default;
        bool ic = opts.IgnoreCase;
        var includes = new WildcardPattern[include.Length];
        for (int i = 0; i < include.Length; i++)
            includes[i] = WildcardPattern.Compile(include[i], ic);

        WildcardPattern[]? excludes = null;
        if (exclude is { Length: > 0 })
        {
            excludes = new WildcardPattern[exclude.Length];
            for (int i = 0; i < exclude.Length; i++)
                excludes[i] = WildcardPattern.Compile(exclude[i], ic);
        }

        return new FilePathMatcher(includes, excludes, opts);
    }

    /// <summary>
    /// Returns true if the file contains at least one matching line.
    /// Stops at the first match for maximum performance.
    /// </summary>
    public bool ContainsMatch(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            return false;

        long fileLength = fileInfo.Length;
        if (fileLength > int.MaxValue)
        {
            // For very large files, fall back to Scan and check count
            var results = new List<LineMatch>();
            ScanFileLargeMapped(filePath, fileLength, results);
            return results.Count > 0;
        }

        return ContainsMatchMemoryMapped(filePath, fileLength);
    }

    private unsafe bool ContainsMatchMemoryMapped(string filePath, long fileLength)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            ptr += accessor.PointerOffset;
            var fileSpan = new ReadOnlySpan<byte>(ptr, (int)fileLength);

            int bomOffset = 0;
            if (fileSpan.Length >= 3 && fileSpan[0] == 0xEF && fileSpan[1] == 0xBB && fileSpan[2] == 0xBF)
                bomOffset = 3;

            var data = fileSpan[bomOffset..];

            // Fast path: byte-level buffer search (no need to extract lines)
            if (_bytePreFilterEnabled && _excludes is null)
            {
                var needle = _bytePrefix ?? _byteSuffix!;
                if (_bytePreFilterShape == WildcardPattern.PatternShape.StarContainsStar)
                {
                    // Just check if the needle exists anywhere in the buffer
                    return _ignoreCase
                        ? IndexOfIgnoreCase(data, needle) >= 0
                        : data.IndexOf(needle) >= 0;
                }
                // For other shapes, use ScanBytesFast but stop at first match
                var results = new List<LineMatch>();
                ScanBytesFast(filePath, data, results, firstMatchOnly: true);
                return results.Count > 0;
            }

            // Fall back to line-by-line scan, stop at first match
            var charBuffer = ArrayPool<char>.Shared.Rent(65536);
            try
            {
                int lineNumber = 0;
                int pos = 0;
                var singleResult = new List<LineMatch>();

                while (pos < data.Length)
                {
                    int nlIdx = data[pos..].IndexOf((byte)'\n');
                    if (nlIdx < 0)
                    {
                        var lastLine = data[pos..];
                        if (lastLine.Length > 0)
                        {
                            lineNumber++;
                            ProcessLine(filePath, lastLine, lineNumber, charBuffer, singleResult);
                            if (singleResult.Count > 0) return true;
                        }
                        break;
                    }

                    lineNumber++;
                    var lineBytes = data.Slice(pos, nlIdx);
                    ProcessLine(filePath, lineBytes, lineNumber, charBuffer, singleResult);
                    if (singleResult.Count > 0) return true;
                    pos += nlIdx + 1;
                }
                return false;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>
    /// Scans files on disk, returning all matching lines with file paths and line numbers.
    /// Files are processed in parallel.
    /// </summary>
    public List<LineMatch> Scan(params string[] filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Length == 0) return [];

        if (filePaths.Length == 1)
        {
            var results = new List<LineMatch>();
            ScanFile(filePaths[0], results);
            return results;
        }

        // Parallel: per-file results, merge preserving file order
        var perFile = new List<LineMatch>[filePaths.Length];
        Parallel.For(0, filePaths.Length, i =>
        {
            var local = new List<LineMatch>();
            ScanFile(filePaths[i], local);
            perFile[i] = local;
        });

        int total = 0;
        foreach (var list in perFile) total += list.Count;
        var merged = new List<LineMatch>(total);
        foreach (var list in perFile)
            merged.AddRange(list);
        return merged;
    }

    /// <summary>
    /// Asynchronously scans files, streaming matches as they are found.
    /// </summary>
    public async IAsyncEnumerable<LineMatch> ScanAsync(
        string[] filePaths,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Length == 0) yield break;

        var channel = Channel.CreateBounded<LineMatch>(new BoundedChannelOptions(1024)
        {
            SingleWriter = false,
            SingleReader = true,
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(filePaths, cancellationToken, (filePath, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var local = new List<LineMatch>();
                    ScanFile(filePath, local);
                    foreach (var match in local)
                        channel.Writer.TryWrite(match);
                    return ValueTask.CompletedTask;
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var match in channel.Reader.ReadAllAsync(cancellationToken))
            yield return match;

        await producer;
    }

    // --- Core: scan a single file using memory-mapped I/O ---

    private void ScanFile(string filePath, List<LineMatch> results)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            return;

        long fileLength = fileInfo.Length;

        if (fileLength <= int.MaxValue)
        {
            ScanFileMemoryMapped(filePath, fileLength, results);
        }
        else
        {
            // Files > 2GB: scan in sections
            ScanFileLargeMapped(filePath, fileLength, results);
        }
    }

    private unsafe void ScanFileMemoryMapped(string filePath, long fileLength, List<LineMatch> results)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            ptr += accessor.PointerOffset;
            var fileSpan = new ReadOnlySpan<byte>(ptr, (int)fileLength);

            // Detect and skip UTF-8 BOM
            int bomOffset = 0;
            if (fileSpan.Length >= 3 && fileSpan[0] == 0xEF && fileSpan[1] == 0xBB && fileSpan[2] == 0xBF)
                bomOffset = 3;

            ScanBytes(filePath, fileSpan[bomOffset..], results);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private unsafe void ScanFileLargeMapped(string filePath, long fileLength, List<LineMatch> results)
    {
        // For files > 2GB, process in 1GB overlapping sections
        const long sectionSize = 1L * 1024 * 1024 * 1024;
        const int overlapSize = 1024 * 1024; // 1MB overlap to handle lines spanning section boundaries

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

        var charBuffer = ArrayPool<char>.Shared.Rent(65536);
        try
        {
            long offset = 0;
            int lineNumber = 0;
            int pendingSkipBytes = 0; // bytes to skip at start of next section (already processed)
            bool firstSection = true;

            while (offset < fileLength)
            {
                long remaining = fileLength - offset;
                long viewSize = Math.Min(sectionSize + (offset > 0 ? overlapSize : 0), remaining);

                using var accessor = mmf.CreateViewAccessor(offset, viewSize, MemoryMappedFileAccess.Read);
                byte* ptr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    ptr += accessor.PointerOffset;
                    var sectionSpan = new ReadOnlySpan<byte>(ptr, (int)viewSize);

                    int startOffset = pendingSkipBytes;

                    // BOM detection on first section
                    if (firstSection && sectionSpan.Length >= 3 &&
                        sectionSpan[0] == 0xEF && sectionSpan[1] == 0xBB && sectionSpan[2] == 0xBF)
                    {
                        startOffset = Math.Max(startOffset, 3);
                        firstSection = false;
                    }

                    var scanSpan = sectionSpan[startOffset..];
                    int linesBeforeScan = lineNumber;

                    // Scan lines in this section
                    int pos = 0;
                    while (pos < scanSpan.Length)
                    {
                        int nlIdx = scanSpan[pos..].IndexOf((byte)'\n');
                        if (nlIdx < 0)
                        {
                            // No more newlines in this section
                            if (offset + viewSize >= fileLength)
                            {
                                // Last section: process remaining as final line
                                var lastLine = scanSpan[pos..];
                                if (lastLine.Length > 0)
                                {
                                    lineNumber++;
                                    ProcessLine(filePath, lastLine, lineNumber, charBuffer, results);
                                }
                            }
                            else
                            {
                                // More sections: the bytes from pos onward will be re-read
                                pendingSkipBytes = 0;
                            }
                            break;
                        }

                        lineNumber++;
                        var lineBytes = scanSpan.Slice(pos, nlIdx);
                        ProcessLine(filePath, lineBytes, lineNumber, charBuffer, results);
                        pos += nlIdx + 1;
                    }

                    // Calculate next offset: advance by sectionSize, not viewSize
                    long processed = startOffset + pos;
                    offset += processed;
                    pendingSkipBytes = 0;
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private void ScanBytes(string filePath, ReadOnlySpan<byte> data, List<LineMatch> results)
    {
        // Buffer-first fast path: search the entire buffer for the needle bytes,
        // then extract lines only around hits. Avoids per-line overhead for non-matching lines.
        if (_bytePreFilterEnabled && _excludes is null)
        {
            ScanBytesFast(filePath, data, results);
            return;
        }

        var charBuffer = ArrayPool<char>.Shared.Rent(65536);
        try
        {
            int lineNumber = 0;
            int pos = 0;

            while (pos < data.Length)
            {
                int nlIdx = data[pos..].IndexOf((byte)'\n');
                if (nlIdx < 0)
                {
                    // Final line without trailing newline
                    var lastLine = data[pos..];
                    if (lastLine.Length > 0)
                    {
                        lineNumber++;
                        ProcessLine(filePath, lastLine, lineNumber, charBuffer, results);
                    }
                    break;
                }

                lineNumber++;
                var lineBytes = data.Slice(pos, nlIdx);
                ProcessLine(filePath, lineBytes, lineNumber, charBuffer, results);
                pos += nlIdx + 1;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    /// <summary>
    /// Buffer-first search: searches the entire buffer for pattern bytes using SIMD-accelerated
    /// IndexOf, then extracts lines only around hits. Much faster than line-first for sparse matches.
    /// </summary>
    private void ScanBytesFast(string filePath, ReadOnlySpan<byte> data, List<LineMatch> results, bool firstMatchOnly = false)
    {
        var needle = _bytePrefix ?? _byteSuffix!;
        var charBuffer = ArrayPool<char>.Shared.Rent(65536);
        try
        {
            int pos = 0;       // current search position in buffer
            int lastPos = 0;   // position after the last processed line's \n
            int lineNumber = 0;

            while (pos < data.Length)
            {
                // Find next occurrence of the needle bytes in the buffer (SIMD-vectorized)
                int hitIdx;
                if (_ignoreCase)
                    hitIdx = IndexOfIgnoreCase(data[pos..], needle);
                else
                    hitIdx = data[pos..].IndexOf(needle);
                if (hitIdx < 0) break;
                hitIdx += pos;

                // Find the enclosing line boundaries
                int lineStart = data[..hitIdx].LastIndexOf((byte)'\n');
                lineStart = lineStart < 0 ? 0 : lineStart + 1;

                // Skip if we already emitted this line (multiple hits on same line)
                if (lineStart < lastPos && lastPos > 0)
                {
                    pos = hitIdx + 1;
                    continue;
                }

                int afterHit = hitIdx + needle.Length;
                int nlAfter = afterHit < data.Length ? data[afterHit..].IndexOf((byte)'\n') : -1;
                int lineEnd = nlAfter < 0 ? data.Length : afterHit + nlAfter;

                // Extract line bytes (excluding the \n)
                var lineBytes = data[lineStart..lineEnd];

                // Strip trailing \r
                if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
                    lineBytes = lineBytes[..^1];

                // Validate match based on pattern shape
                bool valid = _bytePreFilterShape switch
                {
                    WildcardPattern.PatternShape.StarContainsStar => true, // needle found = match
                    WildcardPattern.PatternShape.PureLiteral =>
                        lineBytes.Length == needle.Length,
                    WildcardPattern.PatternShape.PrefixStar =>
                        hitIdx == lineStart, // needle must be at line start
                    WildcardPattern.PatternShape.StarSuffix =>
                        hitIdx + needle.Length == lineEnd || // needle at line end
                        (lineEnd > 0 && data[lineEnd - 1] == (byte)'\r' && hitIdx + needle.Length == lineEnd - 1),
                    WildcardPattern.PatternShape.PrefixStarSuffix =>
                        hitIdx == lineStart && (_ignoreCase
                            ? EndsWithIgnoreCase(lineBytes, _byteSuffix)
                            : lineBytes.EndsWith(_byteSuffix)),
                    _ => true,
                };

                if (!valid)
                {
                    // For PrefixStar/PureLiteral: skip to next line, not just next byte
                    pos = lineEnd + 1;
                    // Count skipped newlines
                    lineNumber += data[lastPos..Math.Min(pos, data.Length)].Count((byte)'\n');
                    lastPos = pos;
                    continue;
                }

                // Count newlines between last position and current line start (SIMD-vectorized)
                lineNumber += data[lastPos..lineStart].Count((byte)'\n') + 1;
                lastPos = lineEnd + 1;

                // Decode and emit
                int charCount = DecodeToChars(lineBytes, charBuffer, out var bigBuffer);
                try
                {
                    var span = bigBuffer is not null ? bigBuffer.AsSpan(0, charCount) : charBuffer.AsSpan(0, charCount);
                    results.Add(new LineMatch(filePath, lineNumber, span.ToString()));
                }
                finally
                {
                    if (bigBuffer is not null) ArrayPool<char>.Shared.Return(bigBuffer);
                }

                if (firstMatchOnly) return;

                // Advance past this line
                pos = lineEnd + 1;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x == y) continue;
            byte xl = (byte)(x | 0x20), yl = (byte)(y | 0x20);
            if (xl >= (byte)'a' && xl <= (byte)'z' && xl == yl) continue;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        if (data.Length < prefix.Length) return false;
        return EqualsIgnoreCase(data[..prefix.Length], prefix);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EndsWithIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> suffix)
    {
        if (data.Length < suffix.Length) return false;
        return EqualsIgnoreCase(data[^suffix.Length..], suffix);
    }

    /// <summary>
    /// Case-insensitive byte search: finds the first occurrence of needle in data,
    /// comparing ASCII letters case-insensitively.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return 0;
        if (data.Length < needle.Length) return -1;

        byte first = needle[0];
        byte firstLower = (byte)(first | 0x20);
        bool firstIsLetter = firstLower >= (byte)'a' && firstLower <= (byte)'z';

        int searchLen = data.Length - needle.Length + 1;
        int pos = 0;

        while (pos < searchLen)
        {
            // Find next candidate for the first byte
            int idx;
            if (firstIsLetter)
                idx = data[pos..].IndexOfAny(firstLower, (byte)(firstLower & ~0x20));
            else
                idx = data[pos..].IndexOf(first);
            if (idx < 0) return -1;
            pos += idx;
            if (pos + needle.Length > data.Length) return -1;

            // Verify remaining bytes
            bool match = true;
            for (int i = 1; i < needle.Length; i++)
            {
                byte a = data[pos + i];
                byte b = needle[i];
                if (a == b) continue;
                byte al = (byte)(a | 0x20);
                byte bl = (byte)(b | 0x20);
                if (al >= (byte)'a' && al <= (byte)'z' && al == bl) continue;
                match = false;
                break;
            }

            if (match) return pos;
            pos++;
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessLine(string filePath, ReadOnlySpan<byte> lineBytes, int lineNumber, char[] charBuffer, List<LineMatch> results)
    {
        // Strip trailing \r
        if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
            lineBytes = lineBytes[..^1];

        // Minimum length pre-filter (byte length >= char length for UTF-8)
        if (lineBytes.Length < _minLineLength)
            return;

        // Byte-level pre-filter: avoid decoding non-matching lines
        if (_bytePreFilterEnabled)
        {
            bool byteMatch;
            if (_ignoreCase)
            {
                byteMatch = _bytePreFilterShape switch
                {
                    WildcardPattern.PatternShape.StarContainsStar =>
                        IndexOfIgnoreCase(lineBytes, _bytePrefix) >= 0,
                    WildcardPattern.PatternShape.PureLiteral =>
                        lineBytes.Length == _bytePrefix!.Length && EqualsIgnoreCase(lineBytes, _bytePrefix),
                    WildcardPattern.PatternShape.StarSuffix =>
                        EndsWithIgnoreCase(lineBytes, _byteSuffix),
                    WildcardPattern.PatternShape.PrefixStar =>
                        StartsWithIgnoreCase(lineBytes, _bytePrefix),
                    WildcardPattern.PatternShape.PrefixStarSuffix =>
                        StartsWithIgnoreCase(lineBytes, _bytePrefix) && EndsWithIgnoreCase(lineBytes, _byteSuffix),
                    _ => true,
                };
            }
            else
            {
                byteMatch = _bytePreFilterShape switch
                {
                    WildcardPattern.PatternShape.StarContainsStar =>
                        lineBytes.IndexOf(_bytePrefix) >= 0,
                    WildcardPattern.PatternShape.PureLiteral =>
                        lineBytes.Length == _bytePrefix!.Length && lineBytes.SequenceEqual(_bytePrefix),
                    WildcardPattern.PatternShape.StarSuffix =>
                        lineBytes.EndsWith(_byteSuffix),
                    WildcardPattern.PatternShape.PrefixStar =>
                        lineBytes.StartsWith(_bytePrefix),
                    WildcardPattern.PatternShape.PrefixStarSuffix =>
                        lineBytes.StartsWith(_bytePrefix) && lineBytes.EndsWith(_byteSuffix),
                    _ => true,
                };
            }

            if (!byteMatch) return;

            // Byte filter is exact for ASCII/UTF-8 — skip IsLineMatch when no excludes
            if (_excludes is null)
            {
                int c = DecodeToChars(lineBytes, charBuffer, out var big);
                try
                {
                    var span = big is not null ? big.AsSpan(0, c) : charBuffer.AsSpan(0, c);
                    results.Add(new LineMatch(filePath, lineNumber, span.ToString()));
                }
                finally
                {
                    if (big is not null) ArrayPool<char>.Shared.Return(big);
                }
                return;
            }
        }

        // Decode to chars
        int charCount = DecodeToChars(lineBytes, charBuffer, out var bigBuffer);
        try
        {
            var lineChars = bigBuffer is not null ? bigBuffer.AsSpan(0, charCount) : charBuffer.AsSpan(0, charCount);
            if (IsLineMatch(lineChars))
                results.Add(new LineMatch(filePath, lineNumber, lineChars.ToString()));
        }
        finally
        {
            if (bigBuffer is not null) ArrayPool<char>.Shared.Return(bigBuffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeToChars(ReadOnlySpan<byte> lineBytes, char[] charBuffer, out char[]? rentedBuffer)
    {
        if (lineBytes.Length <= charBuffer.Length)
        {
            rentedBuffer = null;
            return _encoding.GetChars(lineBytes, charBuffer);
        }

        rentedBuffer = ArrayPool<char>.Shared.Rent(lineBytes.Length);
        return _encoding.GetChars(lineBytes, rentedBuffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsLineMatch(ReadOnlySpan<char> line)
    {
        // Include first: any match = included
        bool included;
        if (_singleInclude is not null)
        {
            included = _singleInclude.IsMatch(line);
        }
        else
        {
            included = false;
            foreach (var pattern in _includes)
            {
                if (pattern.IsMatch(line))
                {
                    included = true;
                    break;
                }
            }
        }

        if (!included) return false;

        // Exclude: any match = rejected
        if (_excludes is not null)
        {
            foreach (var pattern in _excludes)
            {
                if (pattern.IsMatch(line))
                    return false;
            }
        }

        return true;
    }

    private static int ComputeMinLength(WildcardPattern[] includes)
    {
        int min = int.MaxValue;
        foreach (var p in includes)
            min = Math.Min(min, p.MinLength);
        return min == int.MaxValue ? 0 : min;
    }
}
