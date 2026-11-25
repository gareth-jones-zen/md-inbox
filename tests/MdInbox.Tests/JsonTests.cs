using System.Text.Json;

namespace MdInbox.Tests;

/// <summary>list --json and due --json on a temporary folder of made-up lists.</summary>
public sealed class JsonTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "md-inbox-json-" + Guid.NewGuid().ToString("N"));

    private const string Settings = "folder = \"inbox\"\nskip_sections = [\"How to use\"]\nlog_file = \"md-inbox.log\"\n";

    private const string Waiting = """
        # Waiting

        ## How to use

        - **Not an item:** a convention.

        ## Waiting

        - **2026-11-20:** **[Waiting: Cleo Marsh, 2026-11-24] The café floor plan.** Asked on Teams — résumé to follow.
          - **22 Nov: chased.** Next: Tue 24 Nov.
            More about the chase.
          - **Second update.**
            - A deeper note.

        - **2026-11-21:** **[Action, 30 Nov 2026] Book the van.** No one named.

        ## Recently closed

        - ✔ **Closed (2026-11-19): it arrived.** — **2026-11-10:** **[Waiting: Ola Brandt, 15 Nov 2026] The keys.** Detail.
        """;

    private const string Mine = """
        # Mine

        ## Actions

        - **2026-11-01:** **[Action] Long-running thing.**
          - **Wed 25 Nov 2026: check it again.**

        ## Someday

        *None.*
        """;

    public JsonTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "inbox"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);
        File.WriteAllText(Path.Combine(dir, "inbox", "waiting.md"), Waiting.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");   // CRLF
        File.WriteAllText(Path.Combine(dir, "inbox", "mine.md"), Mine + "\n");
        File.WriteAllText(Path.Combine(dir, "inbox", "empty.md"), "# Empty\n\n## Things\n\n*None.*\n");
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

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args],
            new CliHost { Out = output, Err = errors, Env = _ => null, Today = () => new DateOnly(2026, 11, 25) });
        return (code, output.ToString(), errors.ToString());
    }

    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement;

    private static string? S(JsonElement e, string name) => e.GetProperty(name).GetString();

    [Fact]
    public void List_json_gives_each_list_and_its_items_numbered_as_list_does()
    {
        var (code, output, _) = Run("list", "--json");
        Assert.Equal(0, code);
        var lists = Parse(output).GetProperty("lists").EnumerateArray().ToList();
        Assert.Equal(["empty", "mine", "waiting"], lists.Select(l => S(l, "name")));   // an empty list too
        Assert.Equal("inbox/waiting.md", S(lists[2], "file"));
        Assert.Equal(["Waiting"], lists[2].GetProperty("sections").EnumerateArray().Select(s => s.GetString()));
        Assert.Empty(lists[0].GetProperty("items").EnumerateArray());

        var items = lists[2].GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal(1, first.GetProperty("n").GetInt32());
        Assert.Equal("Waiting", S(first, "section"));
        Assert.Equal("2026-11-20", S(first, "date"));
        Assert.Equal("Waiting: Cleo Marsh, 2026-11-24", S(first, "tag"));
        Assert.Equal("The café floor plan", S(first, "title"));
        Assert.Equal("2026-11-24", S(first, "due"));   // the chase date in the tag
        Assert.Equal("Cleo Marsh", S(first, "who"));
        Assert.Equal("**2026-11-20:** **[Waiting: Cleo Marsh, 2026-11-24] The café floor plan.** Asked on Teams — résumé to follow.", S(first, "text"));
        Assert.Equal(["**22 Nov: chased.** Next: Tue 24 Nov.\nMore about the chase.", "**Second update.**\n- A deeper note."],
            first.GetProperty("subitems").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(9, first.GetProperty("line").GetInt32());   // 1-based, CRLF or not
        Assert.False(first.GetProperty("closed").GetBoolean());

        var second = items[1];
        Assert.Equal(("Book the van", "2026-11-30", JsonValueKind.Null), (S(second, "title"), S(second, "due"), second.GetProperty("who").ValueKind));
        Assert.Empty(second.GetProperty("subitems").EnumerateArray());
        Assert.Equal(15, second.GetProperty("line").GetInt32());

        var mine = lists[1].GetProperty("items")[0];
        Assert.Equal(("2026-11-25", "Action"), (S(mine, "due"), S(mine, "tag")));   // a dated sub-bullet
    }

    [Fact]
    public void An_item_number_from_list_json_closes_that_item()
    {
        var items = Parse(Run("list", "waiting", "--json").Out).GetProperty("lists")[0].GetProperty("items");
        var n = items[1].GetProperty("n").GetInt32();
        Assert.Equal(0, Run("close", "waiting", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "booked").Code);
        Assert.Contains("Closed (2026-11-25): booked.** — **2026-11-21:** **[Action, 30 Nov 2026] Book the van.",
            File.ReadAllText(Path.Combine(dir, "inbox", "waiting.md")));
    }

    [Fact]
    public void List_json_keeps_the_filters_and_closed_items()
    {
        var waiting = Parse(Run("list", "--json", "--tag", "waiting").Out).GetProperty("lists");
        Assert.Single(waiting[2].GetProperty("items").EnumerateArray());
        Assert.Empty(waiting[1].GetProperty("items").EnumerateArray());

        var closed = Parse(Run("list", "wait", "--closed", "--json").Out).GetProperty("lists")[0].GetProperty("items")[0];
        Assert.Equal(("c1", true, "2026-11-19", "The keys"), (S(closed, "ref"), closed.GetProperty("closed").GetBoolean(), S(closed, "closedOn"), S(closed, "title")));
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("due").ValueKind);   // due doesn't look at closed items
    }

    [Fact]
    public void List_json_is_valid_when_there_is_nothing()
    {
        File.Delete(Path.Combine(dir, "inbox", "waiting.md"));
        File.Delete(Path.Combine(dir, "inbox", "mine.md"));
        File.Delete(Path.Combine(dir, "inbox", "empty.md"));
        Assert.Equal(0, Parse(Run("list", "--json").Out).GetProperty("lists").GetArrayLength());
    }

    [Fact]
    public void Due_json_says_what_due_prints_with_who_and_when_raised()
    {
        var plain = Run("due").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var (code, output, _) = Run("due", "--json");
        Assert.Equal(0, code);
        var root = Parse(output);
        Assert.Equal(("2026-11-25", 2), (S(root, "today"), root.GetProperty("days").GetInt32()));
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(plain.Length, items.Count);

        var waiting = items[0];
        Assert.Equal(("waiting", 1, "2026-11-24", true, false), (S(waiting, "list"), waiting.GetProperty("n").GetInt32(), S(waiting, "due"),
            waiting.GetProperty("overdue").GetBoolean(), waiting.GetProperty("dueToday").GetBoolean()));
        Assert.Equal(("Cleo Marsh", "2026-11-20", 9), (S(waiting, "who"), S(waiting, "raised"), waiting.GetProperty("dueLine").GetInt32()));

        var line = items[1];   // a dated sub-bullet: due goes by that line
        Assert.Equal(("mine", "2026-11-25", false, true), (S(line, "list"), S(line, "due"), line.GetProperty("overdue").GetBoolean(), line.GetProperty("dueToday").GetBoolean()));
        Assert.Equal(("**Wed 25 Nov 2026: check it again.**", 6, 5), (S(line, "dueText"), line.GetProperty("dueLine").GetInt32(), line.GetProperty("line").GetInt32()));
        Assert.Equal(JsonValueKind.Null, line.GetProperty("raised").ValueKind);   // no one named: no raised

        Assert.DoesNotContain(items, i => S(i, "title") == "Book the van");   // 30 Nov: past the two days
        Assert.Equal(3, Parse(Run("due", "--json", "--days", "5").Out).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Due_json_is_valid_when_nothing_is_due()
    {
        var root = Parse(Run("due", "--json", "--as-of", "2026-11-01").Out);
        Assert.Equal(("2026-11-01", 0), (S(root, "today"), root.GetProperty("items").GetArrayLength()));
    }

    [Fact]
    public void Json_is_unescaped_utf8_text_and_only_for_list_and_due()
    {
        Assert.Contains("café", Run("list", "--json").Out);   // written as it is, not é
        Assert.Equal(2, Run("show", "waiting", "1", "--json").Code);
    }
}
