namespace MdInbox.Tests;

/// <summary>md-inbox on a temporary folder of lists shaped like the real ones.</summary>
public sealed class InboxTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "md-inbox-tests-" + Guid.NewGuid().ToString("N"));

    private const string Settings =
        "folder = \"inbox\"\nskip_sections = [\"How to use\"]\nlog_file = \"md-inbox.log\"\n";

    private const string People = """
        # One-to-ones

        *Items for each person's 1:1.*

        ---

        ## Ann

        - **2026-11-06:** **[Ann 1:1] Wed 11 Nov: the NetBox query.** In essence, a question.
          - **12 Nov: didn't come up.** Carry it.

        - **2026-10-08:** **[Ann 1:1] Follow up the [OAuth client](https://example.com) that expires.** Detail.

        ## Bob

        *Nothing yet.*

        ## Recently closed

        *Closed in the last two weeks.*

        - ✔ **Closed (2026-11-12): asked in her 11 Nov 1:1.** Done. — **2026-11-06:** **[Ann 1:1] Ask how the move went.** Detail.

        - ✔ **Closed (2026-11-01): done — and more.** — **2026-10-20:** **[Bob 1:1] His title.** Detail.
        """;

    private const string Mine = """
        # Mine

        ## How to use

        - **Not an item:** a convention.

        ## Actions

        - **Wed 18 Nov 2026: [Action] Send the forecast.** Due soon.
        - **2026-11-01:** **[Action] Long-running thing.**
          - **Fri 20 Nov 2026: check it again.**
        - **2026-11-12:** **[Waiting: Ann Lee, 18 Nov 2026] The router features.** Asked in her 1:1.
          - **Chased 16 Nov (Teams).** Next: 18 Nov.

        ## Recently closed

        *None.*
        """;

    public InboxTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "inbox"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);
        File.WriteAllText(Path.Combine(dir, "inbox", "one-to-ones.md"), People + "\n");
        File.WriteAllText(Path.Combine(dir, "inbox", "mine.md"), Mine + "\n");
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

    private Config Config => MdInbox.Config.Load(Path.Combine(dir, "config.toml"));

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args],
            new CliHost { Out = output, Err = errors, Env = _ => null, Today = () => new DateOnly(2026, 11, 17) });
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public void Items_are_top_level_bullets_with_what_is_indented_under_them()
    {
        var list = Inbox.Read(Path.Combine(dir, "inbox", "one-to-ones.md"), Config);
        Assert.Equal(["Ann", "Bob"], list.Sections);
        Assert.Equal(2, list.Open.Count);
        Assert.Equal(2, list.Open[0].Lines.Count);   // the indented update is part of it
        Assert.Equal(("Ann 1:1", "Wed 11 Nov: the NetBox query", new DateOnly(2026, 11, 6)),
            (list.Open[0].Tag, list.Open[0].Title, list.Open[0].Date));
        Assert.Equal("Follow up the OAuth client that expires", list.Open[1].Title);   // a link isn't a tag
        Assert.Equal(2, list.Closed.Count);
        Assert.Equal(("Ann 1:1", "Ask how the move went", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 6)),
            (list.Closed[0].Tag, list.Closed[0].Title, list.Closed[0].ClosedOn, list.Closed[0].Date));
        Assert.Equal("His title", list.Closed[1].Title);   // a " — " inside the reason doesn't fool it
    }

    [Fact]
    public void Skipped_sections_hold_no_items()
    {
        var list = Inbox.Read(Path.Combine(dir, "inbox", "mine.md"), Config);
        Assert.Equal(["Actions"], list.Sections);
        Assert.Equal(3, list.Open.Count);
        Assert.Empty(list.Closed);
    }

    [Fact]
    public void List_numbers_open_items_under_their_sections()
    {
        var (code, output, _) = Run("list", "one");
        Assert.Equal(0, code);
        Assert.Equal(
            "one-to-ones\n  Ann\n       1. 2026-11-06  [Ann 1:1] Wed 11 Nov: the NetBox query\n" +
            "       2. 2026-10-08  [Ann 1:1] Follow up the OAuth client that expires\n", output);
        Assert.Contains("c2. 2026-11-01  [Bob 1:1] His title", Run("list", "--closed").Out);
        Assert.DoesNotContain("one-to-ones", Run("list", "--tag", "Action").Out);
    }

    [Fact]
    public void Show_prints_an_item_in_full()
    {
        var (_, output, _) = Run("show", "one-to-ones", "1");
        Assert.StartsWith("one-to-ones / Ann\n\n- **2026-11-06:**", output);
        Assert.Contains("  - **12 Nov: didn't come up.**", output);
        Assert.Contains("Ask how the move went", Run("show", "one-to-ones", "c1").Out);
        Assert.Contains("there's no 9", Run("show", "one-to-ones", "9").Err);
        Assert.Contains("No list called 'nope'", Run("list", "nope").Err);
    }

    [Fact]
    public void Due_finds_dated_lines_in_open_items()
    {
        var due = Inbox.DueItems(Inbox.Lists(Config), new DateOnly(2026, 11, 17), 2);
        Assert.Equal([new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18)], due.Select(d => d.When));   // a line, and a tag
        Assert.Equal(3, Inbox.DueItems(Inbox.Lists(Config), new DateOnly(2026, 11, 17), 3).Count);   // the sub-bullet too
        Assert.Contains("Wed 18 Nov", Run("due").Out);
        Assert.Contains("Nothing due.", Run("due", "--as-of", "2026-11-01").Out);
    }

    [Fact]
    public void A_date_in_a_tag_makes_it_due_and_due_says_who_and_how_long()
    {
        var output = Run("due").Out;
        Assert.Contains("Wed 18 Nov          mine 3: [Waiting] The router features  (Ann Lee; raised 12 Nov, 5 days ago)\n", output);
        Assert.Contains("Send the forecast\n", output);   // no who, nothing added
        Assert.Contains("overdue mine 3", Run("due", "--as-of", "2026-11-19").Out);
        Assert.Contains("[Waiting: Ann Lee, 18 Nov 2026] The router features", Run("list", "--tag", "waiting").Out);
    }

    [Theory]
    [InlineData("Waiting: Ann Lee, 20 Nov 2026", "Waiting", "Ann Lee", "2026-11-20")]
    [InlineData("Action, 25 November 2026", "Action", null, "2026-11-25")]
    [InlineData("Ann 1:1", "Ann 1:1", null, null)]
    [InlineData("Waiting: Ann Lee", "Waiting", "Ann Lee", null)]
    [InlineData("Lee, Ann", "Lee, Ann", null, null)]
    public void Tags_come_apart_into_name_who_and_date(string tag, string name, string? who, string? when)
    {
        var parts = TagParts.Of(tag);
        Assert.Equal((name, who, when is null ? (DateOnly?)null : DateOnly.Parse(when, System.Globalization.CultureInfo.InvariantCulture)),
            (parts.Name, parts.Who, parts.When));
    }

    [Fact]
    public void Archive_moves_old_closed_items_word_for_word_newest_first()
    {
        var archive = Path.Combine(dir, "inbox", "closed", "2026-11.md");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllText(archive, "# Closed inbox items: November 2026\n\n*Intro.*\n\n- ✔ **Closed (2026-11-02): older.** — x\n");

        Assert.Contains("archive: 1 closed item(s) due", Run("archive", "--dry-run").Out);   // 1 Nov, 16 days before the 17th
        Assert.Contains("Ask how the move went", File.ReadAllText(Path.Combine(dir, "inbox", "one-to-ones.md")));   // a dry run moves nothing
        var (code, output, _) = Run("archive", "--as-of", "2026-11-30");
        Assert.Equal(0, code);
        Assert.Contains("archive: 2 closed item(s) moved", output);

        var people = File.ReadAllText(Path.Combine(dir, "inbox", "one-to-ones.md"));
        Assert.DoesNotContain("Ask how the move went", people);
        Assert.Contains("## Recently closed\n\n*Closed in the last two weeks.*\n\n*None.*\n", people);
        Assert.Contains("- **2026-10-08:** **[Ann 1:1] Follow up", people);   // open items untouched

        var text = File.ReadAllText(archive);
        Assert.StartsWith("# Closed inbox items: November 2026\n\n*Intro.*\n\n- ✔ **Closed (2026-11-12): asked", text);
        Assert.True(text.IndexOf("(2026-11-12)", StringComparison.Ordinal) < text.IndexOf("(2026-11-01)", StringComparison.Ordinal));
        Assert.True(text.IndexOf("(2026-11-01)", StringComparison.Ordinal) < text.IndexOf("(2026-11-02): older", StringComparison.Ordinal));
        Assert.Equal(0, Run("archive", "--as-of", "2026-11-30").Code);   // nothing left: no change
    }

    [Fact]
    public void A_new_month_file_gets_a_heading()
    {
        File.WriteAllText(Path.Combine(dir, "inbox", "mine.md"), Mine.Replace("*None.*", "- ✔ **Closed (2026-10-03): sent.** — **2026-09-01:** **[Action] Old.**") + "\n");
        Run("archive", "--as-of", "2026-11-30");
        var text = File.ReadAllText(Path.Combine(dir, "inbox", "closed", "2026-10.md"));
        Assert.StartsWith("# Closed inbox items: October 2026\n\n*Items closed in October 2026, newest first", text);
        Assert.Contains("**[Action] Old.**", text);
    }

    [Fact]
    public void Settings_are_read_strictly()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "colour = \"red\"\n");
        Assert.Contains("unknown setting 'colour'", Run("list").Err);
        File.WriteAllText(Path.Combine(dir, "config.toml"), "skip_sections = []\n");
        Assert.Contains("folder is needed", Run("list").Err);
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "daily_at = \"6am\"\n");
        Assert.Contains("daily_at should be a time", Run("list").Err);
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "[git]\nrepo = \".\"\nbranch = \"x\"\n");
        Assert.Equal(2, Run("list").Code);
    }
}
