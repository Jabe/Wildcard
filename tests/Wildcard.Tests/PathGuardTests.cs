using Wildcard.Mcp.Tools;

namespace Wildcard.Tests;

public class PathGuardTests : IDisposable
{
    private readonly string _tempDir;

    public PathGuardTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), ".test_pathguard_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Null_Input_Returns_CWD()
    {
        var result = PathGuard.Resolve(null);
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        Assert.StartsWith(cwd, result);
    }

    [Fact]
    public void Valid_Subdirectory_Returns_Resolved_Path()
    {
        var sub = Path.Combine(_tempDir, "child");
        Directory.CreateDirectory(sub);

        var result = PathGuard.Resolve(sub);
        Assert.Equal(sub, result);
    }

    [Fact]
    public void Path_Outside_Root_Throws()
    {
        var outside = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../.."));

        var ex = Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(outside));
        Assert.Contains("outside the allowed root", ex.Message);
    }

    [Fact]
    public void Relative_Traversal_Outside_Root_Throws()
    {
        // Deep enough traversal to escape CWD
        var escaped = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../../.."));

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(escaped));
    }

    [Fact]
    public void Root_Itself_Is_Allowed()
    {
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        var result = PathGuard.Resolve(root);
        Assert.Equal(root, result);
    }

    [Fact]
    public void File_Symlink_Outside_Root_Throws()
    {
        // Create a target outside the allowed root
        var outsideDir = Path.Combine(Path.GetTempPath(), "pathguard_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(outsideFile, "secret");

        try
        {
            var linkPath = Path.Combine(_tempDir, "link.txt");
            File.CreateSymbolicLink(linkPath, outsideFile);

            Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(linkPath));
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Directory_Symlink_Outside_Root_Throws()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "pathguard_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var linkPath = Path.Combine(_tempDir, "dirlink");
            Directory.CreateSymbolicLink(linkPath, outsideDir);

            Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(linkPath));
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Symlink_Inside_Root_Is_Allowed()
    {
        var target = Path.Combine(_tempDir, "real");
        Directory.CreateDirectory(target);

        var linkPath = Path.Combine(_tempDir, "link");
        Directory.CreateSymbolicLink(linkPath, target);

        var result = PathGuard.Resolve(linkPath);
        Assert.Equal(target, result);
    }

    // ── Adversarial / breakout attempts ──────────────────────────────

    [Fact]
    public void Traversal_DotDotSlash_Hidden_In_Subpath_Throws()
    {
        // legit/../../.. — descend then climb back out
        var cwd = Directory.GetCurrentDirectory();
        var attack = Path.GetFullPath(Path.Combine(cwd, "a", "b", "..", "..", ".."));

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(attack));
    }

    [Fact]
    public void Prefix_Collision_Not_Actually_Child_Throws()
    {
        // If root is /foo/bar/, the path /foo/bar-evil is NOT a child
        // even though it shares the prefix "/foo/bar"
        var cwd = Directory.GetCurrentDirectory();
        var sibling = cwd + "-evil";

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(sibling));
    }

    [Fact]
    public void Trailing_Slash_Variants_Of_Root_Are_Allowed()
    {
        var cwd = Directory.GetCurrentDirectory();
        var withSlash = cwd + Path.DirectorySeparatorChar;

        var result = PathGuard.Resolve(withSlash);
        Assert.StartsWith(cwd, result);
    }

    [Fact]
    public void Null_Byte_In_Path_Does_Not_Bypass()
    {
        // Null-byte injection: "legit\0/../../../etc"
        // Path.GetFullPath should throw on embedded nulls
        var cwd = Directory.GetCurrentDirectory();
        var attack = Path.Combine(cwd, "safe\0/../../../etc/passwd");

        // .NET throws ArgumentException for embedded null chars — never reaches our check
        Assert.ThrowsAny<Exception>(() => PathGuard.Resolve(attack));
    }

    [Fact]
    public void Empty_String_Throws()
    {
        // Empty string → Path.GetFullPath("") throws ArgumentException — never reaches our check
        Assert.ThrowsAny<ArgumentException>(() => PathGuard.Resolve(""));
    }

    [Fact]
    public void Double_Slash_Does_Not_Confuse()
    {
        var cwd = Directory.GetCurrentDirectory();
        var attack = cwd + "//../../..";

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(attack));
    }

    [Fact]
    public void Dot_Segments_Resolve_But_Stay_Blocked()
    {
        // /allowed/./../../secret → resolves above root
        var cwd = Directory.GetCurrentDirectory();
        var attack = Path.Combine(cwd, ".", "..", "..");

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(attack));
    }

    [Fact]
    public void Deeply_Nested_Traversal_Still_Blocked()
    {
        // Go deep inside root, then climb out further
        var cwd = Directory.GetCurrentDirectory();
        var attack = Path.Combine(cwd, "a", "b", "c", "d", "..", "..", "..", "..", "..", "..");

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(attack));
    }

    [Fact]
    public void Symlink_Chain_Escaping_Root_Throws()
    {
        // A → B → /tmp/outside  (multi-hop symlink chain)
        var outsideDir = Path.Combine(Path.GetTempPath(), "pathguard_chain_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var hopB = Path.Combine(_tempDir, "hop_b");
            Directory.CreateSymbolicLink(hopB, outsideDir);

            var hopA = Path.Combine(_tempDir, "hop_a");
            Directory.CreateSymbolicLink(hopA, hopB);

            Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(hopA));
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dangling_Symlink_Outside_Root_Still_Throws()
    {
        // Symlink to non-existent target outside root — must still block
        var outsidePath = Path.Combine(Path.GetTempPath(), "pathguard_ghost_" + Guid.NewGuid().ToString("N")[..8], "no_such_file");

        var linkPath = Path.Combine(_tempDir, "dangling");
        File.CreateSymbolicLink(linkPath, outsidePath);

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(linkPath));
    }

    [Fact]
    public void Absolute_Path_To_System_Root_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve("/"));
    }

    [Fact]
    public void Absolute_Path_To_Tmp_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve("/tmp"));
    }

    [Fact]
    public void Absolute_Path_To_Etc_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve("/etc"));
    }

    [Fact]
    public void Case_Variant_Of_Root_Parent_Throws()
    {
        // On case-insensitive FS (macOS/Windows), try mixed-case parent escape
        var cwd = Directory.GetCurrentDirectory();
        var parent = Path.GetDirectoryName(cwd)!;
        var caseVariant = parent.ToUpperInvariant();

        // If the case-mangled parent is actually outside root, it must throw
        if (!Path.GetFullPath(caseVariant).Equals(Path.GetFullPath(cwd), StringComparison.OrdinalIgnoreCase))
            Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(caseVariant));
    }

    [Fact]
    public void Relative_Path_Resolves_Against_CWD()
    {
        // "subdir" with no leading slash — should resolve relative to CWD
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "pathguard_rel_test"));

        try
        {
            var result = PathGuard.Resolve("pathguard_rel_test");
            Assert.Contains("pathguard_rel_test", result);
        }
        finally
        {
            try { Directory.Delete(Path.Combine(Directory.GetCurrentDirectory(), "pathguard_rel_test")); } catch { }
        }
    }

    [Fact]
    public void Relative_DotDot_Escape_Throws()
    {
        // Plain "../.." — resolve relative to CWD, should escape
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve("../.."));
    }

    [Fact]
    public void Symlink_Pointing_To_Root_Parent_Via_Child_Traversal_Throws()
    {
        // Symlink target is a path that uses traversal: /allowed-root/child/../../
        var outsideDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".."));
        var linkPath = Path.Combine(_tempDir, "sneaky");
        Directory.CreateSymbolicLink(linkPath, outsideDir);

        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.Resolve(linkPath));
    }
}
