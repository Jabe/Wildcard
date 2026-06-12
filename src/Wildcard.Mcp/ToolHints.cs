namespace Wildcard.Mcp;

/// <summary>
/// Diagnostic hints appended to empty tool results. For LLM consumers a false-empty
/// result is worse than any loud error — errors get corrected, empty results get believed.
/// Hints are only attached to zero-hit responses to keep the noise down.
/// </summary>
public static class ToolHints
{
    /// <summary>Hint for content patterns containing '|', a common regex-alternation habit.</summary>
    public const string PipeInPattern =
        "Note: '|' is matched literally — wildcard syntax has no regex alternation; pass multiple content_patterns for OR.";

    /// <summary>Hint for replace find text containing '|'.</summary>
    public const string PipeInFind =
        "Note: '|' is matched literally — wildcard syntax has no regex alternation.";

    /// <summary>Hint when a glob matched nothing while .gitignore filtering was active.</summary>
    public const string GitignoreActive =
        "Note: .gitignore was respected — pass respect_gitignore=false if the file may be ignored.";
}
