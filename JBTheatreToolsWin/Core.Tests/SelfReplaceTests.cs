using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>The file side of the launcher's in-place update (SelfReplace): the running exe steps aside as
/// "&lt;name&gt;.old", the new build takes its exact path and name, a failed swap changes nothing, and clean-up only
/// ever deletes that exe's own old copies.</summary>
public class SelfReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jbtt-selfreplace-" + Guid.NewGuid().ToString("N"));

    public SelfReplaceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Write(string name, string text) { var p = Path.Combine(_dir, name); File.WriteAllText(p, text); return p; }

    [Fact]
    public void SwapKeepsTheExesNameAndPath()
    {
        // The user's copy has whatever name they gave it; the download has the release's asset name.
        var exe = Write("My Launcher.exe", "old");
        var staged = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staged);
        var newFile = Path.Combine(staged, "JBTheatreTools-Windows-x64.exe");
        File.WriteAllText(newFile, "new");

        var old = JBTheatreTools.SelfReplace.Swap(newFile, exe, JBTheatreTools.SelfReplace.Sha256(newFile), "v2");

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal(exe + ".old", old);
        Assert.Equal("old", File.ReadAllText(old));
        Assert.False(File.Exists(newFile));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void AFileThatChangedAfterVerificationIsNotSwappedIn()
    {
        var exe = Write("JBTheatreTools.exe", "old");
        var newFile = Write("staged.exe", "verified");
        var verifiedHash = JBTheatreTools.SelfReplace.Sha256(newFile);
        File.WriteAllText(newFile, "tampered");   // replaced between verification and the swap

        Assert.ThrowsAny<IOException>(() => JBTheatreTools.SelfReplace.Swap(newFile, exe, verifiedHash, "v2"));
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Empty(JBTheatreTools.SelfReplace.OldCopies(_dir, "JBTheatreTools.exe"));
    }

    [Fact]
    public void FailedSwapPutsTheOldExeBack()
    {
        var exe = Write("JBTheatreTools.exe", "old");
        var missing = Path.Combine(_dir, "not-downloaded.exe");

        Assert.ThrowsAny<IOException>(() => JBTheatreTools.SelfReplace.Swap(missing, exe, "00", "v2"));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Empty(JBTheatreTools.SelfReplace.OldCopies(_dir, "JBTheatreTools.exe"));
    }

    [Fact]
    public void AFolderThatCantBeChangedIsReportedAndNothingMoves()
    {
        if (OperatingSystem.IsWindows()) return;   // (Windows ACLs aren't settable portably here; the VM runs elevated anyway)
        var locked = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(locked);
        var exe = Path.Combine(locked, "JBTheatreTools.exe");
        File.WriteAllText(exe, "old");
        var newFile = Write("staged.exe", "new");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // like Program Files for a user
        try
        {
            var ex = Assert.Throws<JBTheatreTools.UpdateLocationException>(() => JBTheatreTools.SelfReplace.Swap(newFile, exe, JBTheatreTools.SelfReplace.Sha256(newFile), "v2"));
            Assert.Equal(locked, ex.Folder);
            Assert.Equal("old", File.ReadAllText(exe));
            Assert.True(File.Exists(newFile));   // still staged: the window saves it to Downloads instead
            Assert.False(File.Exists(exe + ".new"));
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void AnOldCopyStillInUseGetsANumberedName()
    {
        var exe = Write("JBTheatreTools.exe", "current");
        var busy = Write("JBTheatreTools.exe.old", "still running");
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))   // held open, as a running exe is
        {
            var free = JBTheatreTools.SelfReplace.FreeOldPath(exe);
            if (OperatingSystem.IsWindows()) Assert.Equal(exe + ".2.old", free);   // Windows can't delete an open file
            else Assert.Equal(exe + ".old", free);                                // elsewhere the delete succeeds
        }
    }

    [Fact]
    public void CleanUpDeletesOnlyThisExesOldCopies()
    {
        Write("JBTheatreTools.exe", "current");
        Write("JBTheatreTools.exe.old", "a");
        Write("JBTheatreTools.exe.3.old", "b");
        Write("JBTheatreTools.exe.new", "interrupted");
        Write("JBTheatreTools.exe.config", "keep");        // not an old copy
        Write("Other.exe.old", "keep");                    // another program's
        Write("JBTheatreTools.exe.x.old", "keep");         // not the numbered pattern

        Assert.Equal(3, JBTheatreTools.SelfReplace.OldCopies(_dir, "JBTheatreTools.exe").Count);
        JBTheatreTools.SelfReplace.DeleteOldCopies(_dir, "JBTheatreTools.exe", attempts: 1, delayMs: 0);

        Assert.False(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.old")));
        Assert.False(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.3.old")));
        Assert.False(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.new")));
        Assert.True(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe")));
        Assert.True(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.config")));
        Assert.True(File.Exists(Path.Combine(_dir, "Other.exe.old")));
        Assert.True(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.x.old")));
    }

    [Fact]
    public void RollBackPutsTheOldExeBack()
    {
        var exe = Write("JBTheatreTools.exe", "old");
        var newFile = Write("staged.exe", "new but broken");
        var old = JBTheatreTools.SelfReplace.Swap(newFile, exe, JBTheatreTools.SelfReplace.Sha256(newFile), "v2");

        Assert.True(JBTheatreTools.SelfReplace.RollBack(exe, old));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(exe + ".failed"));
    }

    [Fact]
    public void APlainStartOnlyRemovesAnInterruptedCopyNotABackup()
    {
        Write("JBTheatreTools.exe", "current");
        Write("JBTheatreTools.exe.old", "the user's backup");
        Write("JBTheatreTools.exe.new", "interrupted");

        JBTheatreTools.SelfReplace.DeleteOldCopies(_dir, "JBTheatreTools.exe", attempts: 1, delayMs: 0, onlyIncoming: true);

        Assert.True(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.old")));
        Assert.False(File.Exists(Path.Combine(_dir, "JBTheatreTools.exe.new")));
    }

    [Fact]
    public void ReadOnlyOldCopiesAreStillRemoved()
    {
        Write("JBTheatreTools.exe", "current");
        var old = Write("JBTheatreTools.exe.old", "read-only");
        File.SetAttributes(old, FileAttributes.ReadOnly);

        JBTheatreTools.SelfReplace.DeleteOldCopies(_dir, "JBTheatreTools.exe", attempts: 1, delayMs: 0);

        Assert.False(File.Exists(old));
    }

    /// <summary>A stand-in "new launcher": a shell script (Unix test runs) that signals it started, or quits.</summary>
    private string Script(string name, string body)
    {
        var p = Write(name, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return p;
    }

    [Fact]
    public async Task ANewLauncherThatSignalsIsStarted()
    {
        if (OperatingSystem.IsWindows()) return;   // shell-script stand-ins; the real exe was checked on Windows
        var exe = Script("ok.sh", "echo started > \"$(echo \"$JBTT_AFTER_UPDATE\" | cut -d'|' -f3)\"; sleep 2");
        var ready = Path.Combine(_dir, "ready");
        var r = await JBTheatreTools.SelfReplace.StartAndWaitAsync(exe, exe + ".old", ready, TimeSpan.FromSeconds(10));
        Assert.Equal(JBTheatreTools.SelfReplace.StartResult.Started, r);
        Assert.False(File.Exists(ready));
    }

    [Fact]
    public async Task ANewLauncherThatQuitsIsReported()
    {
        if (OperatingSystem.IsWindows()) return;
        var exe = Script("broken.sh", "exit 1");
        var r = await JBTheatreTools.SelfReplace.StartAndWaitAsync(exe, exe + ".old", Path.Combine(_dir, "ready"), TimeSpan.FromSeconds(10));
        Assert.Equal(JBTheatreTools.SelfReplace.StartResult.Quit, r);
    }

    [Fact]
    public async Task ASlowStartIsLeftAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        var exe = Script("slow.sh", "sleep 5");
        var r = await JBTheatreTools.SelfReplace.StartAndWaitAsync(exe, exe + ".old", Path.Combine(_dir, "ready"), TimeSpan.FromSeconds(1));
        Assert.Equal(JBTheatreTools.SelfReplace.StartResult.StillStarting, r);
    }

    [Fact]
    public void OldCopiesMatchesAnExeNameWithRegexCharacters()
    {
        Write("JB (1).exe", "current");
        Write("JB (1).exe.old", "a");
        Assert.Single(JBTheatreTools.SelfReplace.OldCopies(_dir, "JB (1).exe"));
    }
}
