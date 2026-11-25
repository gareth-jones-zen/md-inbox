namespace MdInbox.Tests;

/// <summary>add, close and redate on a temporary folder of lists shaped like the real ones.</summary>
public sealed class EditsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "md-inbox-edits-" + Guid.NewGuid().ToString("N"));

    private const string Settings = """
        folder = "inbox"
        skip_sections = ["How to use"]
        log_file = "md-inbox.log"

        [tags]
        "Ann 1:1" = "people / Ann"
        "Bob 1:1" = "people / Bob"
        Waiting = "waiting"
        Action = "mine / Actions"
        Stray = "people / Nobody"
        """;

    private const string People = """
        # One-to-ones

        *Items for each person's 1:1.*

        ---

        ## Ann

        - **2026-11-06:** **[Ann 1:1] The NetBox query.** A question.
          - **12 Nov: didn't come up.** Carry it.

        - **2026-10-08:** **[Ann 1:1] The OAuth client.** Detail.

        ## Bob

        *Nothing open.*

        ## Recently closed

        *Closed in the last two weeks.*

        - ✔ **Closed (2026-11-12): asked.** — **2026-11-06:** **[Ann 1:1] Ask how the move went.** Detail.
        """;

    private const string Mine = """
        # Mine

        ## How to use

        - **Not an item:** a convention.

        ## Actions

        - **2026-11-13:** **[Action] Send the forecast.** Soon.
        - **2026-11-01:** **[Action] Long-running thing.**

        ## Someday

        - **2026-09-01:** **[Action] One day.**
        """;

    private const string Waiting = """
        # Waiting for

        ## Waiting

        - **2026-11-16:** **[Waiting: Ann Lee, 19 Nov 2026] The router features.** Asked in her 1:1.
        """;

    public EditsTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "inbox"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);
        File.WriteAllText(Path.Combine(dir, "inbox", "people.md"), People + "\n");
        File.WriteAllText(Path.Combine(dir, "inbox", "mine.md"), Mine + "\n");
        File.WriteAllText(Path.Combine(dir, "inbox", "waiting.md"), Waiting + "\n");
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

    private string Read(string list) => File.ReadAllText(Path.Combine(dir, "inbox", list + ".md"));

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args],
            new CliHost { Out = output, Err = errors, Env = _ => null, Today = () => new DateOnly(2026, 11, 18) });
        return (code, output.ToString(), errors.ToString());
    }

    [Theory]
    [InlineData("[Action] Book the room. Before Friday.", "- **2026-11-18:** **[Action] Book the room.** Before Friday.")]
    [InlineData("[Action] Book the room", "- **2026-11-18:** **[Action] Book the room.**")]
    [InlineData("[Waiting: Ann Lee, 25 Nov 2026] [Bob 1:1] The tags? Asked today.",
        "- **2026-11-18:** **[Waiting: Ann Lee, 25 Nov 2026] [Bob 1:1] The tags?** Asked today.")]
    [InlineData("**[Action] Own bold.** Kept.", "- **2026-11-18:** **[Action] Own bold.** Kept.")]
    [InlineData("- **2026-11-01:** **[Action] As written.**", "- **2026-11-01:** **[Action] As written.**")]
    public void Text_takes_the_lists_shape(string text, string expected) =>
        Assert.Equal([expected], Inbox.Format(text, new DateOnly(2026, 11, 18)));

    [Fact]
    public void Later_lines_go_under_the_item() =>
        Assert.Equal(["- **2026-11-18:** **[Action] Plan.**", "  - first", "", "  more"],
            Inbox.Format("\n[Action] Plan\n- first\n\n  more\n\n", new DateOnly(2026, 11, 18)));

    [Fact]
    public void Add_goes_to_the_top_of_the_tags_section_spaced_like_its_items()
    {
        var (code, output, _) = Run("add", "[Ann 1:1] Ask about the rota. It changed.");
        Assert.Equal(0, code);
        Assert.StartsWith("Added to people / Ann:\n\n- **2026-11-18:** **[Ann 1:1] Ask about the rota.** It changed.\n", output);
        Assert.Contains("## Ann\n\n- **2026-11-18:** **[Ann 1:1] Ask about the rota.** It changed.\n\n- **2026-11-06:**", Read("people"));

        Run("add", "[Action] Tight list");   // mine's Actions has no blank lines between items, so none is added
        Assert.Contains("## Actions\n\n- **2026-11-18:** **[Action] Tight list.**\n- **2026-11-13:**", Read("mine"));
    }

    [Fact]
    public void Add_to_an_empty_section_replaces_its_placeholder()
    {
        Run("add", "[Bob 1:1] First one.");
        Assert.Contains("## Bob\n\n- **2026-11-18:** **[Bob 1:1] First one.**\n\n## Recently closed", Read("people"));
        Assert.Equal(2, Inbox.Read(Path.Combine(dir, "inbox", "people.md"), MdInbox.Config.Load(Path.Combine(dir, "config.toml"))).Sections.Count);
    }

    [Fact]
    public void Where_it_goes_comes_from_the_list_section_or_tag_and_is_never_guessed()
    {
        Run("add", "waiting", "[Waiting: Bob Ray, 26 Nov 2026] The licence.");   // a tag's name finds it; the list has one section
        Assert.Contains("## Waiting\n\n- **2026-11-18:** **[Waiting: Bob Ray, 26 Nov 2026] The licence.**\n\n- **2026-11-16:**", Read("waiting"));

        Run("add", "mine", "--section", "some", "Plain text, no tag", "--date", "2026-11-17");
        Assert.Contains("## Someday\n\n- **2026-11-17:** **Plain text, no tag.**\n\n- **2026-09-01:**", Read("mine"));

        Assert.Contains("Which section? Use --section. mine's sections: Actions; Someday.", Run("add", "mine", "No tag").Err);
        Assert.Contains("[Nope] isn't in the settings' [tags], so name the list (tags known: Action, Ann 1:1, Bob 1:1, Stray, Waiting).",
            Run("add", "[Nope] Something").Err);
        Assert.Contains("which people doesn't have", Run("add", "[Stray] Lost").Err);
        Assert.Contains("matches more than one section", Run("add", "mine", "--section", "s", "[Ann 1:1] x").Err);
        Assert.Equal(2, Run("add").Code);
    }

    [Fact]
    public void Add_from_a_file_and_dry_runs_change_nothing()
    {
        var file = Path.Combine(dir, "item.md");
        File.WriteAllText(file, "[Bob 1:1] From a file. Detail.\n- a sub-point\n");
        var before = Read("people");
        Assert.Contains("Would add to people / Bob:", Run("add", "--file", file, "--dry-run").Out);
        Assert.Equal(before, Read("people"));
        Run("add", "--file", file);
        Assert.Contains("- **2026-11-18:** **[Bob 1:1] From a file.** Detail.\n  - a sub-point\n", Read("people"));
    }

    [Fact]
    public void Close_moves_the_item_word_for_word_to_the_top_of_closed()
    {
        var (code, output, _) = Run("close", "people", "1", "--reason", "asked in her 18 Nov 1:1");
        Assert.Equal(0, code);
        Assert.StartsWith("Closed people 1:\n\n- ✔ **Closed (2026-11-18): asked in her 18 Nov 1:1.** — **2026-11-06:** **[Ann 1:1] The NetBox query.**", output);
        var people = Read("people");
        Assert.Contains("## Ann\n\n- **2026-10-08:**", people);
        Assert.Contains("*Closed in the last two weeks.*\n\n- ✔ **Closed (2026-11-18): asked in her 18 Nov 1:1.** — **2026-11-06:** " +
                        "**[Ann 1:1] The NetBox query.** A question.\n  - **12 Nov: didn't come up.** Carry it.\n\n- ✔ **Closed (2026-11-12): asked.**", people);

        Run("close", "people", "1", "--reason", "done!");   // the last in its section: it says so
        Assert.Contains("## Ann\n\n*None.*\n\n## Bob", Read("people"));
        Assert.Contains("- ✔ **Closed (2026-11-18): done!** — **2026-10-08:**", Read("people"));
    }

    [Fact]
    public void Close_makes_a_closed_section_if_there_is_none_and_refuses_what_it_cant_find()
    {
        Run("close", "mine", "3", "--reason", "Dropped");
        Assert.EndsWith("## Someday\n\n*None.*\n\n## Recently closed\n\n- ✔ **Closed (2026-11-18): Dropped.** — **2026-09-01:** **[Action] One day.**\n", Read("mine"));
        Assert.Contains("there's no 9", Run("close", "mine", "9", "--reason", "x").Err);
        Assert.Contains("Say why", Run("close", "mine", "1").Err);
        Assert.Contains("isn't an item number", Run("close", "mine", "one", "--reason", "x").Err);
    }

    [Fact]
    public void Move_within_a_list_goes_to_the_top_or_bottom_spaced_like_the_section()
    {
        var (code, output, _) = Run("move", "mine", "3", "--section", "act");
        Assert.Equal(0, code);
        Assert.StartsWith("Moved mine 3 to mine / Actions:\n\n- **2026-09-01:** **[Action] One day.**\n", output);
        var mine = Read("mine");
        Assert.Contains("## Actions\n\n- **2026-09-01:** **[Action] One day.**\n- **2026-11-13:** **[Action] Send the forecast.** Soon.\n" +
                        "- **2026-11-01:** **[Action] Long-running thing.**\n", mine);   // tight, like its items
        Assert.Contains("## Someday\n\n*None.*", mine);   // the section it left says so

        Run("move", "mine", "1", "--section", "Actions", "--bottom");   // One day, now first, to the end
        Assert.Contains("- **2026-11-01:** **[Action] Long-running thing.**\n- **2026-09-01:** **[Action] One day.**\n", Read("mine"));

        Run("move", "mine", "3", "--section", "some", "--bottom");   // to an empty section: its stand-in goes
        Assert.Contains("## Someday\n\n- **2026-09-01:** **[Action] One day.**\n", Read("mine"));
        Assert.DoesNotContain("*None.*", Read("mine"));
    }

    [Fact]
    public void Move_to_another_list_keeps_every_line_and_notes_it()
    {
        var (code, output, _) = Run("move", "people", "1", "--to", "mine", "--section", "some", "--note", "for later");
        Assert.Equal(0, code);
        Assert.StartsWith("Moved people 1 to mine / Someday (from people / Ann):", output);
        Assert.Contains("## Someday\n\n- **2026-11-06:** **[Ann 1:1] The NetBox query.** A question.\n" +
                        "  - **12 Nov: didn't come up.** Carry it.\n" +
                        "  - **18 Nov 2026: moved to Someday.** for later.\n\n- **2026-09-01:** **[Action] One day.**", Read("mine"));
        Assert.Contains("## Ann\n\n- **2026-10-08:** **[Ann 1:1] The OAuth client.** Detail.\n\n## Bob", Read("people"));
        Assert.DoesNotContain("NetBox", Read("people").Split("## Recently closed")[0]);
    }

    [Fact]
    public void Move_refuses_what_it_cant_place_and_dry_runs_change_nothing()
    {
        Assert.Contains("matches more than one section", Run("move", "mine", "1", "--section", "s").Err);
        Assert.Contains("Which section? Use --section.", Run("move", "mine", "1").Err);
        Assert.Contains("use `close`", Run("move", "people", "1", "--section", "Recently closed").Err);
        Assert.Contains("c1 is a closed item", Run("move", "people", "c1", "--section", "Bob").Err);
        Assert.Contains("there's no 9", Run("move", "mine", "9", "--section", "some").Err);

        var (mine, people) = (Read("mine"), Read("people"));
        var dry = Run("move", "people", "2", "--to", "mine", "--section", "act", "--dry-run");
        Assert.Equal(0, dry.Code);
        Assert.StartsWith("Would move people 2 to mine / Actions (from people / Ann):", dry.Out);
        Assert.Equal(mine, Read("mine"));
        Assert.Equal(people, Read("people"));
    }

    [Fact]
    public void A_list_with_crlf_line_endings_keeps_them()
    {
        var path = Path.Combine(dir, "inbox", "mine.md");
        File.WriteAllText(path, (Mine + "\n").Replace("\n", "\r\n"));
        Run("move", "mine", "3", "--section", "act");
        var text = File.ReadAllText(path);
        Assert.Contains("## Actions\r\n\r\n- **2026-09-01:** **[Action] One day.**\r\n- **2026-11-13:**", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // no bare LF anywhere
    }

    [Fact]
    public void Redate_moves_the_tags_date_and_notes_the_chase()
    {
        var (code, output, _) = Run("redate", "waiting", "1", "2026-11-27", "--note", "chased in Teams");
        Assert.Equal(0, code);
        Assert.StartsWith("Moved waiting 1 to Fri 27 Nov:", output);
        Assert.Contains("- **2026-11-16:** **[Waiting: Ann Lee, 27 Nov 2026] The router features.** Asked in her 1:1.\n" +
                        "  - **18 Nov: chased in Teams.** Next: Fri 27 Nov.\n", Read("waiting"));
        Assert.Contains("Thu 3 Dec", Run("redate", "waiting", "1", "3 Dec 2026", "--dry-run").Out);
        Assert.Contains("27 Nov 2026]", Read("waiting"));   // the dry run changed nothing
        Assert.Contains("tag has no date to move: [Action].", Run("redate", "mine", "1", "2026-11-20").Err);
        Assert.Contains("isn't a date", Run("redate", "waiting", "1", "Friday").Err);
    }

    [Fact]
    public void Redate_keeps_a_tags_date_as_it_was_written()
    {
        File.WriteAllText(Path.Combine(dir, "inbox", "waiting.md"), Waiting.Replace("19 Nov 2026", "2026-11-19", StringComparison.Ordinal) + "\n");
        Assert.Equal(0, Run("redate", "waiting", "1", "27 Nov 2026").Code);
        Assert.Contains("[Waiting: Ann Lee, 2026-11-27] The router features.", Read("waiting"));
    }
}
