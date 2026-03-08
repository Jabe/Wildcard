# Wildcard

A high-performance .NET library for wildcard pattern matching. Provides a lightweight alternative to the full Regex engine, optimized for speed and low memory usage.

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

// Bulk filtering
var pattern = WildcardPattern.Compile("*.cs");
var files = new[] { "app.cs", "readme.md", "test.cs" };
List<string> csharpFiles = WildcardSearch.FilterLines(pattern, files);
// ["app.cs", "test.cs"]

// Parallel bulk filtering for large datasets
string[] results = WildcardSearch.FilterBulk(pattern, largeArray, parallel: true);
```

## How It Works

### 1. Pattern Compilation

`PatternCompiler.Compile` parses a pattern string into an array of `Segment` objects. Each segment is one of four types:

- **Literal** — a fixed string to match exactly (e.g. `".cs"`)
- **Star** — the `*` wildcard, matches any character sequence
- **QuestionMark** — the `?` wildcard, matches exactly one character
- **CharClass** — a character set like `[a-z]`, optionally negated

Consecutive `*` characters are collapsed into a single Star segment during compilation.

For example, the pattern `*.cs` compiles into `[Star, Literal(".cs")]`.

### 2. Matching Engine

`WildcardPattern.MatchCore` walks the segment array and the input string simultaneously using a backtracking algorithm:

- Two pointers track the current position in the segments and the input.
- When a `*` segment is encountered, the engine records its position as a backtrack point and advances to the next segment.
- If a subsequent segment fails to match, the engine backtracks to the last `*` position and tries consuming one more character from the input.
- The match succeeds when all input is consumed and all segments (ignoring trailing `*`s) are satisfied.

This approach avoids the exponential worst-case that naive recursive implementations can hit.

### 3. Performance

- **Zero-copy matching** — uses `ReadOnlySpan<char>` to avoid string allocations during matching.
- **Aggressive inlining** — hot-path methods like `MatchLiteral` and `CharsEqual` use `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- **Parallel bulk operations** — `WildcardSearch.FilterBulk` processes arrays of 1024+ items in parallel using `Parallel.ForEach`.
- **SIMD-accelerated search** — `VectorizedIndexOf` delegates to the runtime's optimized `Span.IndexOf`, which uses SIMD instructions when available.
