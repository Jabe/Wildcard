# Tool Usage

## MANDATORY: Use Wildcard MCP tools

**CRITICAL — DO NOT SKIP:** Before using any native Glob, Grep, or Edit tool, you MUST check whether the Wildcard MCP tools are available and use them instead. This applies to ALL file searching, content searching, and text replacement operations throughout the entire conversation — including the very first tool call.

### Quick mapping

| Operation | NEVER use | ALWAYS use instead |
|---|---|---|
| Find files by pattern | native `Glob` tool | `mcp__wildcard__wildcard_glob` |
| Search file contents | native `Grep` tool | `mcp__wildcard__wildcard_grep` |
| Replace text in files | native `Edit` tool | `mcp__wildcard__wildcard_replace` |
| Watch files for changes | — | `mcp__wildcard__wildcard_watch` |

### Detailed parameter mapping: Glob → wildcard_glob

| Native `Glob` param | Wildcard `wildcard_glob` param | Notes |
|---|---|---|
| `pattern` | `pattern` | Same glob syntax (`**/*.cs`, `src/**/*.ts`) |
| `path` | `base_directory` | Directory to search in |
| — | `count` | Set `true` to return count only (replaces `wc` usage) |
| — | `exclude_paths` | Array of glob patterns to exclude |
| — | `limit` | Max results (default 10000) |
| — | `respect_gitignore` | Honors .gitignore (default true) |

### Detailed parameter mapping: Grep → wildcard_grep

| Native `Grep` param | Wildcard `wildcard_grep` param | Notes |
|---|---|---|
| `pattern` (regex) | `content_patterns` (array) | **Key difference**: Wildcard takes an array of plain/wildcard patterns, NOT regex. Multiple patterns are OR'd |
| `glob` | `pattern` | **Swapped name**: file glob goes in `pattern` |
| `path` | `base_directory` | Directory to search in |
| `-A` | `after_context` | Lines after match |
| `-B` | `before_context` | Lines before match |
| `-C` / `context` | `context` | Lines before and after match |
| `-i` | `ignore_case` | Case-insensitive matching |
| `-n` | — | Line numbers are included by default |
| `output_mode: "files_with_matches"` | `files_only: true` | Return file paths only |
| `output_mode: "count"` | `count: true` | Return match counts per file |
| `output_mode: "content"` | (default) | Return matched lines with context |
| `head_limit` | `limit` | Max matched lines (default 500) |
| `type` | — | No equivalent; use `pattern` with file extension glob instead (e.g. `"**/*.py"`) |
| — | `exclude_paths` | Exclude files matching globs |
| — | `exclude_patterns` | Exclude lines matching patterns |

### Detailed parameter mapping: Edit → wildcard_replace

| Native `Edit` param | Wildcard `wildcard_replace` param | Notes |
|---|---|---|
| `file_path` | `pattern` + `base_directory` | Use a file glob; for a single file use the exact filename |
| `old_string` | `find` | Text to find — plain string or wildcard pattern with `*`, `?`, `[]` for captures |
| `new_string` | `replace` | Replacement text; use `$1`, `$2` for capture groups |
| `replace_all` | — | Wildcard replaces all occurrences by default |
| — | `dry_run` | **Default is `true`** — preview only. Set `false` to actually write changes |
| — | `ignore_case` | Case-insensitive matching |
| — | `exclude_paths` | Exclude files matching globs |
| — | `limit` | Max files to process (default 50) |

**IMPORTANT `wildcard_replace` gotchas:**
- `dry_run` defaults to `true`. You MUST set `dry_run: false` to apply changes.
- For single-file edits, use the specific filename as `pattern` (e.g. `"src/Foo.cs"`).
- The `find` parameter uses plain text or wildcard patterns, NOT regex.

### Bash commands that are also banned

These Bash commands duplicate what the Wildcard MCP tools do. **Never** run them:

| Banned Bash command | Use instead |
|---|---|
| `find` | `mcp__wildcard__wildcard_glob` |
| `ls` (for searching) | `mcp__wildcard__wildcard_glob` |
| `grep` / `rg` | `mcp__wildcard__wildcard_grep` |
| `wc` (line/word counts) | `mcp__wildcard__wildcard_grep` (use `count: true`) |
| `sed` / `awk` (for replacements) | `mcp__wildcard__wildcard_replace` |

`ls` for listing a known directory and `wc` on tool output are fine — the ban applies only when these commands are used as substitutes for file search, content search, or text replacement.

### Rules

1. **Check availability first.** At the start of each conversation, use `ToolSearch` to verify whether the `mcp__wildcard__*` tools are available. If they are, use them exclusively for every matching operation.
2. **No native fallback unless unavailable.** Only fall back to native Glob/Grep/Edit if the MCP tools are genuinely not connected (i.e., not returned by `ToolSearch`).
3. **No Bash workarounds.** Never use the banned Bash commands listed above as a substitute for MCP tools.
4. **Subagents.** When spawning Explore or general-purpose agents, subagents use their own tool set and cannot be forced to use MCP tools — this is acceptable.
