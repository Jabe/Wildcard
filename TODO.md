# Wildcard MCP Tools — Roadmap to 10/10

## 1. Context lines in grep
Add `-C`/`-B`/`-A` style context around matches (like `rg -C 3`). Returns surrounding lines so the caller can understand matches without a separate file read.

## 2. Bulk replace tool (`wildcard_replace`)
Find-and-replace across files matching a glob + content pattern. Supports preview (dry-run) mode. Enables bulk refactoring without editing files one by one.

## 3. Regex support in grep
Add regex as an alternative matching mode alongside wildcard patterns. Enables `\b`, `\d+`, lookaheads, and other patterns that wildcards can't express.

## 4. Expose capture groups
The library already supports `TryMatch()` with capture extraction — expose this via MCP. Useful for pattern-based file renaming, version extraction, etc.

## 5. Watch + action
Let watch trigger a shell command when changes are detected, turning it into a lightweight task runner (e.g. run tests on save).

## 6. File stats / tree view (`wildcard_tree`)
Return directory structure with file sizes, line counts, and last-modified dates for quick codebase orientation.

## 7. Count / aggregate mode
Return match counts per file or totals without dumping all matched lines. Answers "how many files contain X?" directly.
