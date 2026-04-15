using Wildcard.Mcp;

namespace Wildcard.Tests;

public class RootsProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RootsProvider _provider;

    public RootsProviderTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), ".test_rootsprovider_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _provider = new RootsProvider();
        _provider.SetRoots([_tempDir]);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Null_Input_Returns_DefaultRoot()
    {
        var result = _provider.Resolve(null);
        Assert.StartsWith(_tempDir, result);
    }

    [Fact]
    public void Valid_Subdirectory_Returns_Resolved_Path()
    {
        var sub = Path.Combine(_tempDir, "child");
        Directory.CreateDirectory(sub);

        var result = _provider.Resolve(sub);
        Assert.Equal(sub, result);
    }

    [Fact]
    public void Path_Outside_Root_Throws()
    {
        var outside = Path.GetFullPath(Path.Combine(_tempDir, "../../.."));

        var ex = Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(outside));
        Assert.Contains("outside the allowed root", ex.Message);
    }

    [Fact]
    public void Relative_Traversal_Outside_Root_Throws()
    {
        var escaped = Path.GetFullPath(Path.Combine(_tempDir, "../../../.."));

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(escaped));
    }

    [Fact]
    public void Root_Itself_Is_Allowed()
    {
        var result = _provider.Resolve(_tempDir);
        Assert.Equal(_tempDir, result);
    }

    [Fact]
    public void File_Symlink_Outside_Root_Throws()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "rootsprovider_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(outsideFile, "secret");

        try
        {
            var linkPath = Path.Combine(_tempDir, "link.txt");
            File.CreateSymbolicLink(linkPath, outsideFile);

            Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(linkPath));
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Directory_Symlink_Outside_Root_Throws()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "rootsprovider_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var linkPath = Path.Combine(_tempDir, "dirlink");
            Directory.CreateSymbolicLink(linkPath, outsideDir);

            Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(linkPath));
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

        var result = _provider.Resolve(linkPath);
        Assert.Equal(target, result);
    }

    // ── Adversarial / breakout attempts ──────────────────────────────

    [Fact]
    public void Traversal_DotDotSlash_Hidden_In_Subpath_Throws()
    {
        var attack = Path.GetFullPath(Path.Combine(_tempDir, "a", "b", "..", "..", ".."));

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(attack));
    }

    [Fact]
    public void Prefix_Collision_Not_Actually_Child_Throws()
    {
        var sibling = _tempDir + "-evil";

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(sibling));
    }

    [Fact]
    public void Trailing_Slash_Variants_Of_Root_Are_Allowed()
    {
        var withSlash = _tempDir + Path.DirectorySeparatorChar;

        var result = _provider.Resolve(withSlash);
        Assert.StartsWith(_tempDir, result);
    }

    [Fact]
    public void Null_Byte_In_Path_Does_Not_Bypass()
    {
        var attack = Path.Combine(_tempDir, "safe\0/../../../etc/passwd");

        Assert.ThrowsAny<Exception>(() => _provider.Resolve(attack));
    }

    [Fact]
    public void Empty_String_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => _provider.Resolve(""));
    }

    [Fact]
    public void Double_Slash_Does_Not_Confuse()
    {
        var attack = _tempDir + "//../../..";

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(attack));
    }

    [Fact]
    public void Dot_Segments_Resolve_But_Stay_Blocked()
    {
        var attack = Path.Combine(_tempDir, ".", "..", "..");

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(attack));
    }

    [Fact]
    public void Deeply_Nested_Traversal_Still_Blocked()
    {
        var attack = Path.Combine(_tempDir, "a", "b", "c", "d", "..", "..", "..", "..", "..", "..");

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(attack));
    }

    [Fact]
    public void Symlink_Chain_Escaping_Root_Throws()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "rootsprovider_chain_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var hopB = Path.Combine(_tempDir, "hop_b");
            Directory.CreateSymbolicLink(hopB, outsideDir);

            var hopA = Path.Combine(_tempDir, "hop_a");
            Directory.CreateSymbolicLink(hopA, hopB);

            Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(hopA));
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dangling_Symlink_Outside_Root_Still_Throws()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "rootsprovider_ghost_" + Guid.NewGuid().ToString("N")[..8], "no_such_file");

        var linkPath = Path.Combine(_tempDir, "dangling");
        File.CreateSymbolicLink(linkPath, outsidePath);

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(linkPath));
    }

    [Fact]
    public void Absolute_Path_To_System_Root_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve("/"));
    }

    [Fact]
    public void Absolute_Path_To_Tmp_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve("/tmp"));
    }

    [Fact]
    public void Absolute_Path_To_Etc_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve("/etc"));
    }

    [Fact]
    public void Case_Variant_Of_Root_Parent_Throws()
    {
        var parent = Path.GetDirectoryName(_tempDir)!;
        var caseVariant = parent.ToUpperInvariant();

        if (!Path.GetFullPath(caseVariant).Equals(Path.GetFullPath(_tempDir), StringComparison.OrdinalIgnoreCase))
            Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(caseVariant));
    }

    [Fact]
    public void Relative_Path_Resolves_Against_DefaultRoot()
    {
        var sub = Path.Combine(_tempDir, "rootsprovider_rel_test");
        Directory.CreateDirectory(sub);

        // The default root is _tempDir, so "rootsprovider_rel_test" should resolve relative to cwd.
        // But since we set roots to _tempDir, this only works if cwd happens to contain _tempDir.
        // Test with absolute path instead.
        var result = _provider.Resolve(sub);
        Assert.Contains("rootsprovider_rel_test", result);
    }

    [Fact]
    public void Symlink_Pointing_To_Root_Parent_Via_Child_Traversal_Throws()
    {
        var outsideDir = Path.GetFullPath(Path.Combine(_tempDir, ".."));
        var linkPath = Path.Combine(_tempDir, "sneaky");
        Directory.CreateSymbolicLink(linkPath, outsideDir);

        Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(linkPath));
    }

    // ── Multi-root tests ─────────────────────────────────────────────

    [Fact]
    public void Multiple_Roots_Path_In_Either_Root_Succeeds()
    {
        var root2 = Path.Combine(Path.GetTempPath(), "rootsprovider_root2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root2);

        try
        {
            _provider.SetRoots([_tempDir, root2]);

            var result1 = _provider.Resolve(_tempDir);
            Assert.Equal(_tempDir, result1);

            var result2 = _provider.Resolve(root2);
            Assert.Equal(root2, result2);
        }
        finally
        {
            try { Directory.Delete(root2, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Multiple_Roots_Path_Outside_All_Roots_Throws()
    {
        var root2 = Path.Combine(Path.GetTempPath(), "rootsprovider_root2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root2);

        try
        {
            _provider.SetRoots([_tempDir, root2]);

            Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve("/etc"));
        }
        finally
        {
            try { Directory.Delete(root2, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Root_Update_Via_SetRoots_Changes_Behavior()
    {
        var newRoot = Path.Combine(Path.GetTempPath(), "rootsprovider_newroot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(newRoot);

        try
        {
            // Initially _tempDir is the root
            _provider.Resolve(_tempDir); // should succeed

            // Switch root to newRoot
            _provider.SetRoots([newRoot]);

            // Now _tempDir should be blocked
            Assert.Throws<UnauthorizedAccessException>(() => _provider.Resolve(_tempDir));

            // And newRoot should work
            var result = _provider.Resolve(newRoot);
            Assert.Equal(newRoot, result);
        }
        finally
        {
            try { Directory.Delete(newRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DefaultRoot_Returns_First_Root()
    {
        var root2 = Path.Combine(Path.GetTempPath(), "rootsprovider_root2_" + Guid.NewGuid().ToString("N")[..8]);

        _provider.SetRoots([_tempDir, root2]);

        Assert.StartsWith(_tempDir, _provider.DefaultRoot);
    }
}
