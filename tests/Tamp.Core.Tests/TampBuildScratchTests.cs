using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for <see cref="TampBuild.Scratch"/> and the cleanup hook in
/// <see cref="TampBuild.Execute{T}"/> (TAM-202). The Scratch helper is
/// protected, so each test derives a private subclass that re-exposes
/// the needed surface.
/// </summary>
public sealed class TampBuildScratchTests : IDisposable
{
    private readonly string? _prevKeepScratch;

    public TampBuildScratchTests()
    {
        // Snapshot env state — tests below toggle TAMP_KEEP_SCRATCH and we
        // want to leave the caller's process env exactly as we found it.
        _prevKeepScratch = Environment.GetEnvironmentVariable("TAMP_KEEP_SCRATCH");
        Environment.SetEnvironmentVariable("TAMP_KEEP_SCRATCH", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TAMP_KEEP_SCRATCH", _prevKeepScratch);
    }

    private sealed class ScratchExposingBuild : TampBuild
    {
        public AbsolutePath MakeScratch(string? prefix = null) => Scratch(prefix);
    }

    [Fact]
    public void Scratch_Creates_Unique_Directory_On_Disk()
    {
        var b = new ScratchExposingBuild();
        var a = b.MakeScratch();
        var c = b.MakeScratch();
        try
        {
            Assert.True(a.DirectoryExists());
            Assert.True(c.DirectoryExists());
            Assert.NotEqual(a.Value, c.Value);
        }
        finally
        {
            b.CleanUpScratchDirs();
        }
    }

    [Fact]
    public void Scratch_Default_Prefix_Is_TampScratch()
    {
        var b = new ScratchExposingBuild();
        var dir = b.MakeScratch();
        try
        {
            Assert.StartsWith("tamp-scratch-", dir.Name);
        }
        finally
        {
            b.CleanUpScratchDirs();
        }
    }

    [Fact]
    public void Scratch_Honors_Explicit_Prefix()
    {
        var b = new ScratchExposingBuild();
        var dir = b.MakeScratch("msix-staging");
        try
        {
            Assert.StartsWith("msix-staging-", dir.Name);
        }
        finally
        {
            b.CleanUpScratchDirs();
        }
    }

    [Fact]
    public void Scratch_Tracks_Dirs_In_Snapshot_List()
    {
        var b = new ScratchExposingBuild();
        Assert.Empty(b.ScratchDirsSnapshot());
        var a = b.MakeScratch();
        var c = b.MakeScratch();
        try
        {
            var snap = b.ScratchDirsSnapshot();
            Assert.Equal(2, snap.Count);
            Assert.Contains(a, snap);
            Assert.Contains(c, snap);
        }
        finally
        {
            b.CleanUpScratchDirs();
        }
    }

    [Fact]
    public void CleanUpScratchDirs_Deletes_Tracked_Directories()
    {
        var b = new ScratchExposingBuild();
        var a = b.MakeScratch();
        var c = b.MakeScratch();
        Assert.True(a.DirectoryExists());
        Assert.True(c.DirectoryExists());

        b.CleanUpScratchDirs();

        Assert.False(a.DirectoryExists());
        Assert.False(c.DirectoryExists());
        Assert.Empty(b.ScratchDirsSnapshot());
    }

    [Fact]
    public void CleanUpScratchDirs_Deletes_Even_When_Dir_Has_Files()
    {
        var b = new ScratchExposingBuild();
        var dir = b.MakeScratch();
        (dir / "file1.txt").WriteAllText("hi");
        (dir / "sub" / "file2.txt").WriteAllText("nested");

        b.CleanUpScratchDirs();

        Assert.False(dir.DirectoryExists());
    }

    [Fact]
    public void CleanUpScratchDirs_Is_Idempotent()
    {
        var b = new ScratchExposingBuild();
        b.MakeScratch();
        b.CleanUpScratchDirs();
        // Second call must not throw and must not re-delete anything (list is empty).
        b.CleanUpScratchDirs();
        Assert.Empty(b.ScratchDirsSnapshot());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]   // anything non-empty / not "0" / not "false" counts as keep
    public void CleanUpScratchDirs_Preserves_When_TAMP_KEEP_SCRATCH_Set(string envValue)
    {
        Environment.SetEnvironmentVariable("TAMP_KEEP_SCRATCH", envValue);
        var b = new ScratchExposingBuild();
        var dir = b.MakeScratch();
        try
        {
            b.CleanUpScratchDirs();
            Assert.True(dir.DirectoryExists());
            // Snapshot is NOT cleared on keep — preserves the audit trail.
            Assert.NotEmpty(b.ScratchDirsSnapshot());
        }
        finally
        {
            // Manual cleanup since CleanUp was a no-op.
            try { Directory.Delete(dir.Value, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("TAMP_KEEP_SCRATCH", null);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    [InlineData("")]
    public void CleanUpScratchDirs_Treats_FalseLike_Values_As_Cleanup(string envValue)
    {
        Environment.SetEnvironmentVariable("TAMP_KEEP_SCRATCH", envValue);
        var b = new ScratchExposingBuild();
        var dir = b.MakeScratch();
        try
        {
            b.CleanUpScratchDirs();
            Assert.False(dir.DirectoryExists());
        }
        finally
        {
            try { if (Directory.Exists(dir.Value)) Directory.Delete(dir.Value, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("TAMP_KEEP_SCRATCH", null);
        }
    }

    [Fact]
    public void Scratch_Concurrent_Allocations_All_Tracked()
    {
        var b = new ScratchExposingBuild();
        try
        {
            var dirs = Enumerable.Range(0, 25).AsParallel()
                .Select(_ => b.MakeScratch())
                .ToList();
            Assert.Equal(25, dirs.Count);
            Assert.Equal(25, dirs.Select(d => d.Value).Distinct().Count());
            Assert.Equal(25, b.ScratchDirsSnapshot().Count);
        }
        finally
        {
            b.CleanUpScratchDirs();
        }
    }

    [Fact]
    public void Scratch_Dirs_Are_Under_The_Worktree_Not_System_Temp()
    {
        // #14: scratch is worktree-local (RootDirectory/.tamp/temp), NOT the shared OS
        // temp root — so parallel workers in separate worktrees never share a scratch root.
        var b = new ScratchExposingBuild();
        var dir = b.MakeScratch();
        try
        {
            Assert.StartsWith(TampBuild.TemporaryDirectory.Value, dir.Value);
        }
        finally
        {
            b.CleanUpScratchDirs();
        }
    }
}
