using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MdInbox;

/// <summary>`list --json` and `due --json`: the same items as the plain commands, numbered the same way, for another
/// program to read. Written with Utf8JsonWriter (no reflection, so Native AOT is happy); non-ASCII is written as it
/// is, and the console exe writes UTF-8 when redirected (ConsoleText).</summary>
internal static class Json
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keeps "✔" and "—" readable; still escapes quotes and control characters
    };

    /// <summary>The lists, each with its open (or closed) items as `list` numbers them.</summary>
    public static string Lists(Config config, IEnumerable<(InboxList List, IReadOnlyList<Item> Items)> lists) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("lists");
        foreach (var (list, items) in lists)
        {
            w.WriteStartObject();
            w.WriteString("name", list.Name);
            w.WriteString("file", FileName(config, list.Path));
            w.WriteString("path", Path.GetFullPath(list.Path));
            w.WriteStartArray("sections");
            foreach (var section in list.Sections)
                w.WriteStringValue(section);
            w.WriteEndArray();
            w.WriteStartArray("items");
            foreach (var item in items)
            {
                w.WriteStartObject();
                ItemFields(w, item, Inbox.DueOn(item));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>What `due` prints: one entry per dated line, soonest first, with who and when it was raised.</summary>
    public static string Due(DateOnly today, int ahead, IReadOnlyList<Due> due) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("today", Iso(today));
        w.WriteNumber("days", ahead);
        w.WriteStartArray("items");
        foreach (var d in due)
        {
            var who = d.Item.Parts?.Who;
            w.WriteStartObject();
            w.WriteString("list", d.Item.List);
            ItemFields(w, d.Item, d.When);
            w.WriteBoolean("overdue", d.When < today);
            w.WriteBoolean("dueToday", d.When == today);
            Date(w, "raised", who is null ? null : d.Item.Date);
            w.WriteString("dueText", Bullet(d.Line));
            w.WriteNumber("dueLine", d.Item.Start + d.Offset + 1);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>The fields every item has, in both commands.</summary>
    private static void ItemFields(Utf8JsonWriter w, Item item, DateOnly? due)
    {
        w.WriteNumber("n", item.Number);
        w.WriteString("ref", (item.Closed ? "c" : "") + item.Number.ToString(CultureInfo.InvariantCulture));
        w.WriteString("section", item.Section);
        Date(w, "date", item.Date);
        w.WriteString("tag", item.Tag);
        w.WriteString("title", item.Title);
        Date(w, "due", due);
        w.WriteString("who", item.Parts?.Who);
        w.WriteBoolean("closed", item.Closed);
        Date(w, "closedOn", item.Closed ? item.ClosedOn : null);
        w.WriteString("text", item.FirstLine);
        w.WriteStartArray("subitems");
        foreach (var sub in item.SubItems)
            w.WriteStringValue(sub);
        w.WriteEndArray();
        w.WriteString("markdown", item.Text);
        w.WriteNumber("line", item.Start + 1);
    }

    private static void Date(Utf8JsonWriter w, string name, DateOnly? date)
    {
        if (date is { } d)
            w.WriteString(name, Iso(d));
        else
            w.WriteNull(name);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A line without its "- ", if it has one.</summary>
    private static string Bullet(string line) => line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line;

    /// <summary>The list's file relative to the repository ([git]), else to the folder that holds the lists' folder:
    /// "inbox/mine.md", with forward slashes.</summary>
    private static string FileName(Config config, string path)
    {
        var full = Path.GetFullPath(path);
        var root = config.Git?.Repo is { } repo && IsUnder(full, Path.GetFullPath(repo)) ? Path.GetFullPath(repo)
            : Path.GetDirectoryName(Path.GetFullPath(config.Folder).TrimEnd('\\', '/')) ?? Path.GetFullPath(config.Folder);
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
            body(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
