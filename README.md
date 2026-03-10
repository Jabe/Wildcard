# Wildcard

A high-performance .NET 10 library for wildcard pattern matching. Provides a lightweight alternative to the full Regex engine, optimized for speed and zero allocations on the hot path.

## Requirements

- .NET 10.0 or later

## Supported Syntax

| Token | Description |
|-------|-------------|
| `*` | Matches any sequence of characters (including empty) |
| `?` | Matches exactly one character |
| `[abc]` | Matches any character in the set |
| `[a-z]` | Matches any character in the range |
| `[!x]` or `[^x]` | Matches any character NOT in the set |
| `\` | Escapes the next character (e.g. `\*` matches a literal `*`) |

## Usage

```csharp
// One-shot match
bool matches = WildcardPattern.IsMatch("*.cs", "program.cs"); // true

// Pre-compiled pattern (reuse across many inputs)
var pattern = WildcardPattern.Compile("file?.log");
pattern.IsMatch("file1.log"); // true
pattern.IsMatch("fileAB.log"); // false

// Case-insensitive matching
WildcardPattern.IsMatch("HELLO", "hello", ignoreCase: true); // true

// TryMatch — extract what each * captured
var p = WildcardPattern.Compile("\\[*\\] * - *");
if (p.TryMatch("[2024-03-15] ERROR - timeout", out var captures))
{
    // captures: ["2024-03-15", "ERROR", "timeout"]
}

// LINQ extensions
string[] files = ["app.cs", "readme.md", "test.cs", "data.csv"];
var csFiles = files.WhereMatch("*.cs").ToList();       // ["app.cs", "test.cs"]
bool hasCsv = files.AnyMatch(WildcardPattern.Compile("*.csv")); // true
string? first = files.FirstMatch(WildcardPattern.Compile("*.md")); // "readme.md"

// Convert to Regex for interop
Regex regex = WildcardPattern.Compile("*.csv").ToRegex();
// regex.ToString() == "^.*\\.csv$"

// Bulk filtering
var pattern = WildcardPattern.Compile("*.cs");
List<string> csharpFiles = WildcardSearch.FilterLines(pattern, files);

// Parallel bulk filtering for large datasets
string[] results = WildcardSearch.FilterBulk(pattern, largeArray, parallel: true);
```

### File Content Scanning

`FilePathMatcher` scans files on disk for lines matching wildcard patterns using memory-mapped I/O.

```csharp
// Single include pattern
var matcher = FilePathMatcher.Create("*ERROR*");
List<FilePathMatcher.LineMatch> matches = matcher.Scan("app.log", "server.log");

foreach (var match in matches)
    Console.WriteLine($"{match.FilePath}:{match.LineNumber}: {match.Line}");

// Include/exclude patterns — match lines containing ERROR but not DEBUG
var matcher = FilePathMatcher.Create(
    include: ["*ERROR*"],
    exclude: ["*DEBUG*"]
);
var matches = matcher.Scan("app.log");

// Async streaming — results arrive as they are found
var matcher = FilePathMatcher.Create("*timeout*");
await foreach (var match in matcher.ScanAsync(filePaths))
    Console.WriteLine(match.Line);
```

### File System Globbing

`Glob` matches file paths on disk with support for `*`, `?`, `[abc]`, and `**` (recursive directory matching).

```csharp
// Find all .cs files recursively
var files = Glob.Match("src/**/*.cs").ToList();

// Single directory level
var logs = Glob.Match("/var/log/*.log").ToList();

// Respect .gitignore — skips bin/, obj/, node_modules/, .git/ etc.
var tracked = Glob.Match("**/*.cs", options: new GlobOptions { RespectGitignore = true }).ToList();

// Pre-parsed glob for reuse
var glob = Glob.Parse("**/*.json");
foreach (var file in glob.EnumerateMatches("/my/project"))
    Console.WriteLine(file);
```

### CLI Tool — `wcg`

A command-line grep tool built on top of the library. Respects `.gitignore` by default, streams results as they're found.

```
Usage: wcg <glob> [pattern] [options]

  wcg "src/**/*.cs"                     List matching files
  wcg "**/*.log" "*ERROR*"               Search for ERROR in log files
  wcg "**/*.cs" "*TODO*" -x "*DONE*"     Search TODO, exclude DONE
  wcg "**/*.cs" "*TODO*" -i              Case-insensitive search
  wcg "**/*.cs" --no-ignore              Include .gitignore'd files
```

## Benchmarks

Measured on Apple M4 Pro, .NET 10.0, Arm64 RyuJIT AdvSIMD. Zero allocations for all single-match operations.

### Pattern Matching — Wildcard vs Compiled Regex vs FileSystemName

| Pattern | Input | Wildcard | Regex | FSName | Speedup vs Regex |
|---------|-------|----------|-------|--------|-----------------|
| `*.csv` | short (15 chars) | 1.3 ns | 13.0 ns | 4.5 ns | **10x** |
| `*.csv` | long (74 chars) | 1.3 ns | 15.0 ns | 4.5 ns | **12x** |
| `*.csv` | no match | 1.3 ns | 10.5 ns | 4.2 ns | **8x** |
| `report*.csv` | short | 2.0 ns | 13.2 ns | 80.0 ns | **7x** |
| `report_????.csv` | short | 5.2 ns | 8.8 ns | 39.1 ns | **2x** |
| `[rs]*.*` | short | 7.5 ns | 13.2 ns | — | **2x** |
| `*report*2024*` | short | 13.0 ns | 21.9 ns | 188.5 ns | **2x** |
| `*report*2024*` | long | 15.2 ns | 25.6 ns | 994.3 ns | **2x** |

### Real-World Patterns — Wildcard vs Compiled Regex

| Pattern | Scenario | Wildcard | Regex | Speedup |
|---------|----------|----------|-------|---------|
| `v2.*` | version prefix | 0.7 ns | 8.3 ns | **11x** |
| `[[]2024-03-15*` | log date prefix | 1.0 ns | 12.3 ns | **12x** |
| `*@gmail.com` | email domain | 1.4 ns | 10.5 ns | **8x** |
| `[AEIOU]*` (CI) | starts with vowel | 2.4 ns | 6.5 ns | **3x** |
| `J* *Smith` (CI) | no match | 2.5 ns | 6.4 ns | **3x** |
| `??? *` | 3-char first name | 3.2 ns | 6.4 ns | **2x** |
| `SKU-*-BLUE-*` | product code | 9.5 ns | 10.3 ns | **1.1x** |
| `J* *Smith` (CI) | match | 13.6 ns | 33.2 ns | **2x** |
| `*@*.acme-corp.com` | corporate email | 13.7 ns | 38.5 ns | **3x** |
| `*ERROR*timeout*` | log search (no match) | 19.2 ns | 27.9 ns | **1.5x** |
| `*ERROR*timeout*` | log search (match) | 36.8 ns | 47.6 ns | **1.3x** |

### Bulk Filtering — 10,000 Items

| Method | Mean | Allocated |
|--------|------|-----------|
| Wildcard FilterLines | 33 µs | 33 KB |
| Wildcard FilterBulk (parallel) | 61 µs | 174 KB |
| FSName LINQ filter | 68 µs | 10 KB |
| Regex LINQ filter | 198 µs | 11 KB |

### File Content Scanning — `FilePathMatcher`

Pattern `*ERROR*` across 4 log files (~12.5% matching lines). Compared against `File.ReadAllLines` + `FilterLines` baseline.

| File size | Baseline (ReadAllLines) | FilePathMatcher | Ratio | Alloc Ratio |
|-----------|------------------------|-----------------|-------|-------------|
| small (1K lines) | 279 µs | 79 µs | 0.29 | 0.15 |
| medium (100K lines) | 52,811 µs | 7,796 µs | 0.15 | 0.15 |
| large (1M lines) | 558,834 µs | 110,174 µs | 0.20 | 0.15 |

### CLI — `wcg` vs `find`, `grep`, `ripgrep`

Real-world benchmark on `~/Code` (~5.4k .cs files, ~5.3k .json files across multiple git repos). Apple M4 Pro, .NET 10.0.

| Task | `find` | `grep -r` | `rg` | `wcg` |
|------|--------|-----------|------|-------|
| Find all .cs files | 14.7s | — | — | **8.9s** |
| Find all .json files | 16.8s | — | — | **8.5s** |
| Deep glob `**/bin/**/*.dll` | 17.2s | — | — | **7.6s** |
| Search `namespace` in .cs | — | 16.6s | **1.1s** | 4.9s |
| Search `TODO` in .cs | — | 16.2s | **1.1s** | 5.0s |
| Case-insensitive `error` in .json | — | 36.7s | **1.0s** | 4.9s |

`wcg` beats `find` by **~2x** for file discovery and `grep` by **~3x** for content search. `.gitignore` filtering (on by default) prunes `bin/`, `obj/`, `node_modules/` etc. during traversal. Parallelized content scanning overlaps glob enumeration with file I/O.

`ripgrep` remains the fastest content search tool thanks to SIMD-accelerated string matching and parallel directory walking.

## How It Works

### 1. Pattern Compilation

`PatternCompiler.Compile` parses a pattern string into an array of `Segment` objects. Each segment is one of five types:

- **Literal** — a fixed string to match exactly (e.g. `".cs"`)
- **Star** — the `*` wildcard, matches any character sequence
- **QuestionMark** — the `?` wildcard, matches exactly one character
- **QuestionRun** — consecutive `?` characters collapsed into a single segment with a count
- **CharClass** — a character set like `[a-z]`, optionally negated, using `SearchValues<char>` for SIMD-accelerated lookups

Consecutive `*` characters are collapsed into a single Star segment during compilation. Single-character, non-negated character classes (e.g. `[[]`) are promoted to Literal segments and merged with adjacent literals.

### 2. Pattern Shape Specialization

At compile time, common pattern shapes are detected and dispatched to optimized fast-paths that bypass the general matching engine entirely:

| Shape | Example | Fast-path |
|-------|---------|-----------|
| `PureLiteral` | `hello` | `SequenceEqual` |
| `StarSuffix` | `*.csv` | `EndsWith` |
| `PrefixStar` | `v2.*` | `StartsWith` |
| `PrefixStarSuffix` | `report*.csv` | `StartsWith` + `EndsWith` |
| `StarContainsStar` | `*ERROR*` | `IndexOf` |

All other patterns fall through to the general backtracking engine.

### 3. Matching Engine

`MatchCore` walks the segment array and the input string simultaneously using a backtracking algorithm:

- Two pointers track the current position in the segments and the input.
- When a `*` segment is encountered, the engine records its position as a backtrack point and advances to the next segment.
- If a subsequent segment fails to match, the engine backtracks to the last `*` position and tries consuming one more character from the input.
- **IndexOf acceleration** — when a `*` is followed by a literal, the engine uses `Span.IndexOf` to jump directly to the next occurrence instead of scanning character-by-character.
- **EndsWith fast-path** — when a `*` is followed by the final literal segment, the engine checks `EndsWith` instead of scanning.

This approach avoids the exponential worst-case that naive recursive implementations can hit.

### 4. Performance Techniques

- **Zero-copy matching** — uses `ReadOnlySpan<char>` to avoid string allocations during matching.
- **Aggressive inlining** — hot-path methods like `MatchLiteral` and `CharsEqual` use `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- **SIMD-accelerated character classes** — `SearchValues<char>` provides hardware-accelerated membership testing. For case-insensitive patterns, both upper and lower case variants are expanded at compile time so the SIMD path works unconditionally.
- **`ref readonly` struct access** — segments are accessed by reference in the hot loop to avoid copying the struct on each iteration.
- **Parallel bulk operations** — `WildcardSearch.FilterBulk` processes arrays of 1024+ items in parallel using PLINQ with order preservation.

### 5. File Content Scanning

`FilePathMatcher` scans files on disk using memory-mapped I/O and parallel processing:

- **Memory-mapped I/O** — files are mapped directly into memory, avoiding buffered read overhead. Files over 2GB are processed in 1GB overlapping sections.
- **Byte-level pre-filtering** — for ASCII, case-sensitive patterns over UTF-8 data, pattern matching runs directly on raw bytes using SIMD-accelerated span operations (`IndexOf`, `StartsWith`, `EndsWith`). Lines that don't match skip UTF-8 decoding entirely.
- **Minimum length gate** — lines shorter than the pattern's minimum possible match length are rejected before any decoding or matching.
- **Parallel multi-file scanning** — multiple files are scanned concurrently via `Parallel.For`, with results merged preserving file order.
- **Async streaming** — `ScanAsync` uses a bounded channel to stream matches as they are found.

## Attribution

This project was generated with the assistance of [Claude Opus 4.6](https://claude.ai) by [Anthropic](https://anthropic.com).
