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
    /// A line from a scanned file with context information.
    /// </summary>
    public readonly record struct ContextLine(string FilePath, int LineNumber, string Line, bool IsMatch);

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

    // Single-include fast path
    private readonly WildcardPattern? _singleInclude;

    // Byte-level pre-filter for ASCII patterns over UTF-8 (single-include path)
    private readonly byte[]? _bytePrefix;
    private readonly byte[]? _byteSuffix;
    private readonly WildcardPattern.PatternShape _bytePreFilterShape;
    private readonly bool _bytePreFilterEnabled;

    // Per-pattern byte pre-filters for multi-include OR path
    private readonly struct ByteFilter(WildcardPattern.PatternShape shape, byte[]? prefix, byte[]? suffix)
    {
        public readonly WildcardPattern.PatternShape Shape = shape;
        public readonly byte[]? Prefix = prefix;
        public readonly byte[]? Suffix = suffix;
    }
    private readonly ByteFilter[]? _perIncludeByteFilters; // non-null when all includes have usable filters
    private readonly bool _multiBytePreFilterEnabled;

    private FilePathMatcher(WildcardPattern[] includes, WildcardPattern[]? excludes, Options options)
    {
        _includes = includes;
        _excludes = excludes;
        _encoding = options.Encoding ?? Encoding.UTF8;
        _singleInclude = includes.Length == 1 ? includes[0] : null;
        _minLineLength = ComputeMinLength(includes);
        InitBytePreFilter(_singleInclude, _encoding, out _bytePreFilterEnabled, out _bytePreFilterShape, out _bytePrefix, out _byteSuffix);

        // Build per-include byte filters for multi-pattern OR: reject a line only when ALL filters reject it.
        if (includes.Length > 1 && _encoding.CodePage == 65001)
        {
            var filters = new ByteFilter[includes.Length];
            bool allEnabled = true;
            for (int i = 0; i < includes.Length; i++)
            {
                InitBytePreFilter(includes[i], _encoding, out bool en, out var sh, out var pfx, out var sfx);
                if (!en) { allEnabled = false; break; }
                filters[i] = new ByteFilter(sh, pfx, sfx);
            }
            if (allEnabled)
            {
                _perIncludeByteFilters = filters;
                _multiBytePreFilterEnabled = true;
            }
        }
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

        if (pattern is null || pattern.IgnoreCase || encoding.CodePage != 65001)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ByteFilterMatches(ReadOnlySpan<byte> lineBytes, in ByteFilter filter) =>
        filter.Shape switch
        {
            WildcardPattern.PatternShape.StarContainsStar => lineBytes.IndexOf(filter.Prefix) >= 0,
            WildcardPattern.PatternShape.PureLiteral => lineBytes.Length == filter.Prefix!.Length && lineBytes.SequenceEqual(filter.Prefix),
            WildcardPattern.PatternShape.StarSuffix => lineBytes.EndsWith(filter.Suffix),
            WildcardPattern.PatternShape.PrefixStar => lineBytes.StartsWith(filter.Prefix),
            WildcardPattern.PatternShape.PrefixStarSuffix => lineBytes.StartsWith(filter.Prefix) && lineBytes.EndsWith(filter.Suffix),
            _ => true,
        };

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

        if (fileLength <= BufferedReadThreshold)
        {
            return ContainsMatchBuffered(filePath, (int)fileLength);
        }

        if (fileLength > int.MaxValue)
        {
            var results = new List<LineMatch>();
            ScanFileLargeMapped(filePath, fileLength, results);
            return results.Count > 0;
        }

        return ContainsMatchMemoryMapped(filePath, fileLength);
    }

    private bool ContainsMatchBuffered(string filePath, int fileLength)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(fileLength);
        try
        {
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: fileLength, FileOptions.SequentialScan))
                bytesRead = fs.Read(buffer, 0, fileLength);

            var data = buffer.AsSpan(0, bytesRead);

            int bomOffset = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                bomOffset = 3;

            return ContainsMatchInData(data[bomOffset..]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

            return ContainsMatchInData(fileSpan[bomOffset..]);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private bool ContainsMatchInData(ReadOnlySpan<byte> data)
    {
        var charBuffer = ArrayPool<char>.Shared.Rent(65536);
        try
        {
            int pos = 0;
            var singleResult = new List<LineMatch>();

            while (pos < data.Length)
            {
                int nlIdx = data[pos..].IndexOf((byte)'\n');
                ReadOnlySpan<byte> lineBytes;
                bool isLastLine = false;

                if (nlIdx < 0)
                {
                    lineBytes = data[pos..];
                    if (lineBytes.Length == 0) break;
                    isLastLine = true;
                }
                else
                {
                    lineBytes = data.Slice(pos, nlIdx);
                }

                ProcessLine(string.Empty, lineBytes, 0, charBuffer, singleResult);
                if (singleResult.Count > 0) return true;

                if (isLastLine) break;
                pos += nlIdx + 1;
            }
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
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
                await Parallel.ForEachAsync(filePaths, cancellationToken, async (filePath, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var local = new List<LineMatch>();
                    ScanFile(filePath, local);
                    foreach (var match in local)
                        await channel.Writer.WriteAsync(match, ct);
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

    /// <summary>
    /// Scans files returning matching lines with surrounding context lines.
    /// Context lines have IsMatch=false; matched lines have IsMatch=true.
    /// Overlapping context ranges are merged (no duplicate lines).
    /// </summary>
    public List<ContextLine> ScanWithContext(int beforeContext, int afterContext, params string[] filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Length == 0) return [];

        // Zero-context fast path: delegate to Scan and wrap
        if (beforeContext <= 0 && afterContext <= 0)
        {
            var matches = Scan(filePaths);
            var result = new List<ContextLine>(matches.Count);
            foreach (var m in matches)
                result.Add(new ContextLine(m.FilePath, m.LineNumber, m.Line, true));
            return result;
        }

        if (filePaths.Length == 1)
        {
            return ScanFileWithContext(filePaths[0], beforeContext, afterContext);
        }

        // Parallel: per-file results, merge preserving file order
        var perFile = new List<ContextLine>[filePaths.Length];
        Parallel.For(0, filePaths.Length, i =>
        {
            perFile[i] = ScanFileWithContext(filePaths[i], beforeContext, afterContext);
        });

        int total = 0;
        foreach (var list in perFile) total += list.Count;
        var merged = new List<ContextLine>(total);
        foreach (var list in perFile)
            merged.AddRange(list);
        return merged;
    }

    private unsafe List<ContextLine> ScanFileWithContext(string filePath, int beforeContext, int afterContext)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            return [];

        long fileLength = fileInfo.Length;
        if (fileLength > int.MaxValue)
        {
            // For very large files, fall back to Scan results wrapped as ContextLine
            var matches = new List<LineMatch>();
            ScanFileLargeMapped(filePath, fileLength, matches);
            var result = new List<ContextLine>(matches.Count);
            foreach (var m in matches)
                result.Add(new ContextLine(m.FilePath, m.LineNumber, m.Line, true));
            return result;
        }

        // Small files: buffered read
        if (fileLength <= BufferedReadThreshold)
            return ScanFileWithContextFromSpan(filePath, (int)fileLength, beforeContext, afterContext);

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            ptr += accessor.PointerOffset;
            var fileSpan = new ReadOnlySpan<byte>(ptr, (int)fileLength);
            return ScanContextFromData(filePath, fileSpan, beforeContext, afterContext);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private List<ContextLine> ScanFileWithContextFromSpan(string filePath, int fileLength, int beforeContext, int afterContext)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(fileLength);
        try
        {
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: fileLength, FileOptions.SequentialScan))
                bytesRead = fs.Read(buffer, 0, fileLength);

            return ScanContextFromData(filePath, buffer.AsSpan(0, bytesRead), beforeContext, afterContext);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private List<ContextLine> ScanContextFromData(string filePath, ReadOnlySpan<byte> fileSpan, int beforeContext, int afterContext)
    {
        int bomOffset = 0;
        if (fileSpan.Length >= 3 && fileSpan[0] == 0xEF && fileSpan[1] == 0xBB && fileSpan[2] == 0xBF)
            bomOffset = 3;

        var data = fileSpan[bomOffset..];

        // Pass 1: find all matching line numbers
        var matchResults = new List<LineMatch>();
        ScanBytes(filePath, data, matchResults);
        if (matchResults.Count == 0)
            return [];

        var matchLineNumbers = new HashSet<int>(matchResults.Count);
        foreach (var m in matchResults)
            matchLineNumbers.Add(m.LineNumber);

        // Compute merged context intervals
        var intervals = MergeContextIntervals(matchResults, beforeContext, afterContext);

        // Pass 2: collect all lines within context intervals
        return CollectContextLines(filePath, data, matchLineNumbers, intervals);
    }

    private static List<(int Start, int End)> MergeContextIntervals(
        List<LineMatch> matches, int beforeContext, int afterContext)
    {
        var intervals = new List<(int Start, int End)>(matches.Count);
        foreach (var m in matches)
        {
            int start = Math.Max(1, m.LineNumber - beforeContext);
            int end = m.LineNumber + afterContext;
            intervals.Add((start, end));
        }

        // Merge overlapping/adjacent intervals
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)> { intervals[0] };
        for (int i = 1; i < intervals.Count; i++)
        {
            var last = merged[^1];
            if (intervals[i].Start <= last.End + 1)
                merged[^1] = (last.Start, Math.Max(last.End, intervals[i].End));
            else
                merged.Add(intervals[i]);
        }
        return merged;
    }

    private List<ContextLine> CollectContextLines(
        string filePath, ReadOnlySpan<byte> data,
        HashSet<int> matchLineNumbers, List<(int Start, int End)> intervals)
    {
        var results = new List<ContextLine>();
        var charBuffer = ArrayPool<char>.Shared.Rent(65536);
        try
        {
            int lineNumber = 0;
            int pos = 0;
            int intervalIdx = 0;

            while (pos < data.Length && intervalIdx < intervals.Count)
            {
                int nlIdx = data[pos..].IndexOf((byte)'\n');
                ReadOnlySpan<byte> lineBytes;
                bool isLastLine = false;

                if (nlIdx < 0)
                {
                    lineBytes = data[pos..];
                    if (lineBytes.Length == 0) break;
                    isLastLine = true;
                }
                else
                {
                    lineBytes = data.Slice(pos, nlIdx);
                }

                lineNumber++;

                // Skip past intervals we've passed
                while (intervalIdx < intervals.Count && intervals[intervalIdx].End < lineNumber)
                    intervalIdx++;

                if (intervalIdx < intervals.Count &&
                    lineNumber >= intervals[intervalIdx].Start &&
                    lineNumber <= intervals[intervalIdx].End)
                {
                    // This line is within a context interval — decode it
                    var stripped = lineBytes;
                    if (stripped.Length > 0 && stripped[^1] == (byte)'\r')
                        stripped = stripped[..^1];

                    int charCount = DecodeToChars(stripped, charBuffer, out var bigBuffer);
                    try
                    {
                        var lineChars = bigBuffer is not null
                            ? bigBuffer.AsSpan(0, charCount)
                            : charBuffer.AsSpan(0, charCount);
                        results.Add(new ContextLine(filePath, lineNumber, lineChars.ToString(),
                            matchLineNumbers.Contains(lineNumber)));
                    }
                    finally
                    {
                        if (bigBuffer is not null) ArrayPool<char>.Shared.Return(bigBuffer);
                    }
                }

                if (isLastLine) break;
                pos += nlIdx + 1;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
        return results;
    }

    // --- Core: scan a single file ---

    /// <summary>
    /// Files at or below this size use buffered reads instead of memory-mapped I/O.
    /// Avoids per-file mmap kernel overhead (VMA setup, page tables, TLB entries) for small files,
    /// which are the vast majority in typical codebases.
    /// </summary>
    private const long BufferedReadThreshold = 64 * 1024;

    private void ScanFile(string filePath, List<LineMatch> results)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            return;

        long fileLength = fileInfo.Length;

        if (fileLength <= BufferedReadThreshold)
        {
            ScanFileBuffered(filePath, (int)fileLength, results);
        }
        else if (fileLength <= int.MaxValue)
        {
            ScanFileMemoryMapped(filePath, fileLength, results);
        }
        else
        {
            // Files > 2GB: scan in sections
            ScanFileLargeMapped(filePath, fileLength, results);
        }
    }

    private void ScanFileBuffered(string filePath, int fileLength, List<LineMatch> results)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(fileLength);
        try
        {
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: fileLength, FileOptions.SequentialScan))
                bytesRead = fs.Read(buffer, 0, fileLength);

            var data = buffer.AsSpan(0, bytesRead);

            // Detect and skip UTF-8 BOM
            int bomOffset = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                bomOffset = 3;

            ScanBytes(filePath, data[bomOffset..], results);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    // Note: a buffer-first (search-first) approach was tried here — searching the entire mmap'd
    // buffer for needle bytes via SIMD IndexOf, then extracting lines only around hits. It was
    // reverted because filesystem I/O (~3.4s) dominates wall time, making the scanning strategy
    // difference unmeasurable. The existing byte pre-filter in ProcessLine already avoids decoding
    // non-matching lines, which captures most of the benefit with far less complexity.
    private void ScanBytes(string filePath, ReadOnlySpan<byte> data, List<LineMatch> results)
    {
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
            bool byteMatch = _bytePreFilterShape switch
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
        else if (_multiBytePreFilterEnabled)
        {
            // Multi-include OR: skip decoding only when ALL per-pattern byte filters reject this line.
            bool anyPass = false;
            foreach (ref readonly var filter in _perIncludeByteFilters!.AsSpan())
            {
                if (ByteFilterMatches(lineBytes, in filter)) { anyPass = true; break; }
            }
            if (!anyPass) return;
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
