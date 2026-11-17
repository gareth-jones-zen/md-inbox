namespace MdInbox.Tests;

/// <summary>Settings that name a shared base file (base = "base-config.toml").</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "md-inbox-base-" + Guid.NewGuid().ToString("N"));

    private const string Base = """
        keep_days = 30
        login_hint = "me@example.com"

        [paths]
        EA_LOCAL = "C:/Users/me/AppData/Local/claude-ea"

        [git]
        repo = ".."
        push = true
        trailers = ["Co-Authored-By: someone"]
        """;

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "tools"));
        File.WriteAllText(Path.Combine(dir, "tools", "base-config.toml"), Base);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string? Env(string name) => name == "LOCALAPPDATA" ? @"C:\Users\me\AppData\Local" : null;

    private Config Load(string text)
    {
        var path = Path.Combine(dir, "tools", "md-inbox.toml");
        File.WriteAllText(path, text);
        return Config.Load(path, Env);
    }

    [Fact]
    public void Paths_and_known_settings_come_from_the_base()
    {
        var config = Load("base = \"base-config.toml\"\nfolder = \"../inbox\"\nlog_file = \"%EA_LOCAL%/md-inbox.log\"\n");
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-ea\md-inbox.log", config.LogFile);
        Assert.Equal(30, config.KeepDays);   // login_hint isn't one of md-inbox's settings, so it's left out, not an error
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("base = \"base-config.toml\"\nfolder = \"../inbox\"\nkeep_days = 7\n[git]\npush = false\n");
        Assert.Equal(7, config.KeepDays);
        Assert.False(config.Git!.Push);
        Assert.Equal(["Co-Authored-By: someone"], config.Git.Trailers);
    }

    [Fact]
    public void Git_applies_only_where_the_tool_has_the_section()
    {
        Assert.Null(Load("base = \"base-config.toml\"\nfolder = \"../inbox\"\n").Git);
        var git = Load("base = \"base-config.toml\"\nfolder = \"../inbox\"\n[git]\n").Git!;
        Assert.True(git.Push);
        Assert.Equal(dir, git.Repo.TrimEnd('\\'));
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<UsageException>(() => Load("base = \"nope.toml\"\nfolder = \"../inbox\"\n"));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Contains("nope.toml", error.Message);
    }
}
