using System.Diagnostics;

namespace MdInbox.Tests;

/// <summary>[git] with and without a repository: a folder that isn't one is saved and exits 0; a repository that refuses
/// the commit exits 3 for an edit (don't retry) and 1 for the archive.</summary>
public sealed class GitTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "md-inbox-git-" + Guid.NewGuid().ToString("N"));

    private const string Settings = """
        folder = "inbox"
        log_file = "md-inbox.log"
        keep_days = 14

        [tags]
        Action = "mine / Actions"

        [git]
        repo = "."
        push = true
        """;

    private const string Mine = """
        # Mine

        ## Actions

        - **2026-11-13:** **[Action] Send the forecast.** Soon.

        ## Recently closed

        - ✔ **Closed (2026-10-01): sent.** — **2026-09-01:** **[Action] Old.**
        """;

    public GitTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "inbox"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);
        File.WriteAllText(Path.Combine(dir, "inbox", "mine.md"), Mine + "\n");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args],
            new CliHost { Out = output, Err = errors, Env = _ => null, Today = () => new DateOnly(2026, 11, 18) });
        return (code, output.ToString(), errors.ToString());
    }

    private string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private void InitRepo()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("add", "-A");
        Git("commit", "-q", "-m", "First");
    }

    /// <summary>A pre-commit hook that refuses every commit: the repository is set up, but the commit fails.</summary>
    private void RefuseCommits()
    {
        var hooks = Path.Combine(dir, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\necho refused >&2\nexit 1\n");
    }

    [Fact]
    public void A_folder_that_isnt_a_repository_saves_the_edit_and_exits_0()
    {
        var (code, output, _) = Run("add", "[Action] Book the room.");
        Assert.Equal(0, code);
        Assert.Contains("**[Action] Book the room.**", File.ReadAllText(Path.Combine(dir, "inbox", "mine.md")));
        Assert.Contains("git: not committing: ", output);
        Assert.Contains("isn't a git repository", output);
        Assert.DoesNotContain("commit failed", output);
        Assert.False(Directory.Exists(Path.Combine(dir, ".git")));   // never initialised

        var archived = Run("archive");
        Assert.Equal(0, archived.Code);
        Assert.True(File.Exists(Path.Combine(dir, "inbox", "closed", "2026-10.md")));
    }

    [Fact]
    public void A_repository_with_no_upstream_commits_and_doesnt_try_to_push()
    {
        InitRepo();
        var (code, output, _) = Run("add", "[Action] Book the room.");
        Assert.Equal(0, code);
        Assert.Contains("git: not pushing: the branch has no upstream to push to", output);
        Assert.Contains(", not pushed: the branch has no upstream to push to", output);
        Assert.DoesNotContain("push failed", output);
        Assert.Equal("?? md-inbox.log", Git("status", "--porcelain").Trim());   // the list committed; only the log is left
    }

    [Fact]
    public void A_commit_the_repository_refuses_after_an_edit_is_exit_3_and_the_edit_is_saved()
    {
        InitRepo();
        RefuseCommits();
        var (code, output, _) = Run("add", "[Action] Book the room.");
        Assert.Equal(Cli.SavedNotCommitted, code);
        Assert.Equal(3, code);
        Assert.Contains("saved, not committed (don't run it again): git commit failed", output);
        Assert.Contains("**[Action] Book the room.**", File.ReadAllText(Path.Combine(dir, "inbox", "mine.md")));

        Assert.Equal(1, Run("archive").Code);   // the archive's moves can't be repeated by a rerun: 1, as before
    }
}
