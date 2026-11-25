using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MdInbox;

/// <summary>One item: a top-level "- " bullet in a "## " section, with everything indented (or blank) under it. Lines
/// are its text as it is in the file. Number is its place among the list's open items (or closed ones), from 1.</summary>
public sealed partial record Item(string List, string Section, int Number, int Start, int End, IReadOnlyList<string> Lines, bool Closed)
{
    public string Text => string.Join("\n", Lines);

    /// <summary>The first line without its "- ", and, for a closed item, without the "✔ **Closed (date): what
    /// happened.** — " put in front of the original: the original item's own first line.</summary>
    private string Head
    {
        get
        {
            var line = Lines[0].TrimStart()[2..];
            if (Closed && ClosedPrefix().Match(line) is { Success: true } m)
                line = line[(m.Index + m.Length)..];
            return line;
        }
    }

    /// <summary>The first line as written, without its "- " (a closed item's with its "✔ **Closed ...** — " in front).</summary>
    public string FirstLine => Lines[0].TrimStart()[2..];

    /// <summary>The sub-bullets under it ("- ", "* " or "+ "), each without its bullet: the shallowest ones start a
    /// sub-item, and anything under one (deeper bullets, continued lines) joins it on a new line, indented as it is
    /// under that sub-bullet's text. Blank lines, and text before the first sub-bullet, aren't in any.</summary>
    public IReadOnlyList<string> SubItems
    {
        get
        {
            var subs = new List<StringBuilder>();
            int? indent = null;
            foreach (var line in Lines.Skip(1))
            {
                var body = line.TrimStart();
                if (body.Length == 0)
                    continue;
                var lead = line.Length - body.Length;
                if (body is ['-' or '*' or '+', ' ', ..] && (indent is null || lead <= indent))
                {
                    indent = lead;
                    subs.Add(new StringBuilder(body[2..].Trim()));
                }
                else if (subs.Count > 0)
                    subs[^1].Append('\n').Append(line[Math.Min(lead, indent!.Value + 2)..].TrimEnd());
            }
            return subs.Select(s => s.ToString()).ToList();
        }
    }

    /// <summary>The first yyyy-mm-dd on its (original) first line: when it was raised. For a closed item, see ClosedOn.</summary>
    public DateOnly? Date => FirstDate(Head);

    /// <summary>When a closed item closed: the first "Closed (yyyy-mm-dd)", else its first date.</summary>
    public DateOnly? ClosedOn =>
        ClosedDate().Match(Text) is { Success: true } m ? Parse(m.Groups[1].Value) : FirstDate(Text);

    /// <summary>The first [Tag] on its (original) first line (not a markdown link's text), whole: "Waiting: Ann
    /// Lee, 20 Nov 2026".</summary>
    public string? Tag => TagPattern().Match(Head) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>The tag's parts: its name, who it names (after a colon) and its date (after the last comma, when
    /// that's a date). "[Waiting: Ann Lee, 20 Nov 2026]" is ("Waiting", "Ann Lee", 20 Nov 2026); "[Action, 25 Nov
    /// 2026]" is ("Action", null, 25 Nov 2026); "[Ann 1:1]" is ("Ann 1:1", null, null).</summary>
    public TagParts? Parts => Tag is { } tag ? TagParts.Of(tag) : null;

    /// <summary>A one-line title: the bold text holding the tag, else the first bold text that isn't just a date,
    /// else the start of the line; without the tag or markdown.</summary>
    public string Title
    {
        get
        {
            var first = Head;
            var bolds = Bold().Matches(first).Select(m => m.Groups[1].Value).ToList();
            var pick = bolds.FirstOrDefault(b => Tag is { } tag && b.Contains($"[{tag}]", StringComparison.Ordinal))
                       ?? bolds.FirstOrDefault(b => !Leading().IsMatch(b) && !b.StartsWith("Closed", StringComparison.Ordinal))
                       ?? first;
            if (Tag is { } t)
                pick = pick.Replace($"[{t}]", "", StringComparison.Ordinal);
            pick = Markdown().Replace(pick, m => m.Groups[1].Success ? m.Groups[1].Value : "").Trim(' ', '.', ':', '-', '—');
            return pick.Length > 110 ? pick[..107].TrimEnd() + "..." : pick;
        }
    }

    internal static DateOnly? FirstDate(string text) =>
        IsoDate().Match(text) is { Success: true } m ? Parse(m.Groups[1].Value) : null;

    private static DateOnly? Parse(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    /// <summary>Where a closed item's own text starts: after " — " and before its "**YYYY-MM-DD".</summary>
    [GeneratedRegex(@"\s—\s(?=\*\*\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex ClosedPrefix();

    [GeneratedRegex(@"Closed\W{0,3}\(?(\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex ClosedDate();

    [GeneratedRegex(@"\[([A-Za-z][^\]\n""'`]{0,59})\](?!\()", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex Bold();

    /// <summary>A bold piece that's only a date heading: "2026-09-28 — raised 1 Oct:" or "Wed 25 Nov 2026:".</summary>
    [GeneratedRegex(@"^\s*(?:\d{4}-\d{2}-\d{2}|(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\b|\d{1,2} [A-Z][a-z]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex Leading();

    [GeneratedRegex(@"\*\*|(?<!\w)\*(?!\s)|`|\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Markdown();
}

/// <summary>A tag taken apart: see Item.Parts.</summary>
public sealed partial record TagParts(string Name, string? Who, DateOnly? When)
{
    public static TagParts Of(string tag)
    {
        DateOnly? when = null;
        var comma = tag.LastIndexOf(',');
        if (comma > 0 && Inbox.LongDate(tag[(comma + 1)..].Trim()) is { } d)
            (when, tag) = (d, tag[..comma].TrimEnd());
        var colon = WhoColon().Match(tag);
        return colon.Success
            ? new TagParts(tag[..colon.Index].Trim(), tag[(colon.Index + 1)..].Trim() is { Length: > 0 } who ? who : null, when)
            : new TagParts(tag.Trim(), null, when);
    }

    /// <summary>The tag as shown beside a date already given: its name (and who), without the date.</summary>
    public override string ToString() => Who is null ? Name : $"{Name}: {Who}";

    /// <summary>A colon that starts a "who", not one inside a name ("Ann 1:1"): followed by a space.</summary>
    [GeneratedRegex(@":(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex WhoColon();
}

/// <summary>A dated action that's due: an item line starting with a date, such as "- **Wed 25 Nov 2026: ...", or
/// an item whose tag carries one, "[Action, 25 Nov 2026]" or "[Waiting: Ann Lee, 25 Nov 2026]".
/// Offset is the dated line's place in the item (0 for its first line).</summary>
public sealed record Due(DateOnly When, Item Item, string Line, int Offset = 0);

/// <summary>One list: a markdown file in the inbox folder, its "## " sections, and the items in them.</summary>
public sealed class InboxList
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required List<string> Lines { get; init; }
    public required IReadOnlyList<Item> Open { get; init; }
    public required IReadOnlyList<Item> Closed { get; init; }
    public required IReadOnlyList<string> Sections { get; init; }
}

public static partial class Inbox
{
    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[..12];

    /// <summary>The lists in the folder (every *.md directly in it), in name order.</summary>
    public static List<InboxList> Lists(Config config) =>
        Directory.Exists(config.Folder)
            ? Directory.GetFiles(config.Folder, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Read(p, config)).ToList()
            : throw new UsageException($"No inbox folder at {config.Folder}.");

    /// <summary>One list by name (its file name without .md, or enough of it to be unique).</summary>
    public static InboxList List(Config config, string name)
    {
        var all = Lists(config);
        var exact = all.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;
        var some = all.Where(l => l.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return some.Count == 1 ? some[0]
            : throw new UsageException($"No list called '{name}' (there are: {string.Join(", ", all.Select(l => l.Name))}).");
    }

    public static InboxList Read(string path, Config config) =>
        Parse(path, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList(), config);

    /// <summary>A list from its lines (as Read, without reading the file): what an edit re-reads after each change.</summary>
    internal static InboxList Parse(string path, List<string> lines, Config config)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var open = new List<Item>();
        var closed = new List<Item>();
        var sections = new List<string>();
        for (var n = 0; n < lines.Count; n++)
        {
            if (!lines[n].StartsWith("## ", StringComparison.Ordinal))
                continue;
            var section = lines[n][3..].Trim();
            var end = n + 1;
            while (end < lines.Count && !lines[end].StartsWith("## ", StringComparison.Ordinal) && lines[end].Trim() != "---")
                end++;
            var isClosed = section.Equals(config.ClosedHeading, StringComparison.OrdinalIgnoreCase);
            if (!isClosed && config.SkipSections.Contains(section, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!isClosed)
                sections.Add(section);
            var into = isClosed ? closed : open;
            for (var at = n + 1; at < end; at++)
            {
                if (!lines[at].StartsWith("- ", StringComparison.Ordinal))
                    continue;
                var stop = at + 1;
                while (stop < end && (lines[stop].Length == 0 || lines[stop].Trim().Length == 0 || char.IsWhiteSpace(lines[stop][0])))
                    stop++;
                var last = stop;
                while (last > at + 1 && lines[last - 1].Trim().Length == 0)
                    last--;
                into.Add(new Item(name, section, into.Count + 1, at, last, lines.GetRange(at, last - at), isClosed));
                at = stop - 1;
            }
        }
        return new InboxList { Name = name, Path = path, Lines = lines, Open = open, Closed = closed, Sections = sections };
    }

    // ---- due ------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^\s*- \*\*(?:\[Action, )?(?:(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun) )?(\d{1,2} (?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]* \d{4})",
        RegexOptions.CultureInvariant)]
    private static partial Regex Dated();

    /// <summary>"25 Nov 2026" or "25 November 2026" as a date; anything else null.</summary>
    internal static DateOnly? LongDate(string text) =>
        DateOnly.TryParseExact(text, ["d MMM yyyy", "d MMMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Open items with a line starting with a date, or a dated tag on their first line, on or before today
    /// + ahead days, soonest first. A line counts once, whichever way it's dated.</summary>
    public static List<Due> DueItems(IEnumerable<InboxList> lists, DateOnly today, int ahead)
    {
        var found = lists.SelectMany(l => l.Open).SelectMany(DatedLines).Where(d => d.When <= today.AddDays(ahead));
        return found.OrderBy(d => d.When).ThenBy(d => d.Item.List).ToList();
    }

    /// <summary>Every dated line of an item, whatever the date (what due goes by): a line starting with a date, or
    /// for the first line, a dated tag.</summary>
    public static IEnumerable<Due> DatedLines(Item item)
    {
        for (var n = 0; n < item.Lines.Count; n++)
        {
            var line = item.Lines[n];
            var when = Dated().Match(line) is { Success: true } m ? LongDate(m.Groups[1].Value) : null;
            if (when is null && n == 0)
                when = item.Parts?.When;
            if (when is { } w)
                yield return new Due(w, item, line.Trim(), n);
        }
    }

    /// <summary>The date due would act on for an open item: its earliest dated line, or null if it has none.</summary>
    public static DateOnly? DueOn(Item item) =>
        item.Closed ? null : DatedLines(item).Select(d => (DateOnly?)d.When).Min();

    // ---- archive --------------------------------------------------------------------------------------------------

    /// <summary>What an archive run moved: (list, month, item) for each, and the files it changed.</summary>
    public sealed record ArchiveResult(IReadOnlyList<(string List, string Month, Item Item)> Moved, IReadOnlyList<string> Changed);

    /// <summary>Closed items that closed more than keep_days ago move, word for word, to {archive}/YYYY-MM.md for the
    /// month they closed, newest first. A closed section left empty says "*None.*". Nothing is deleted.</summary>
    public static ArchiveResult Archive(Config config, DateOnly today, bool dryRun)
    {
        var moved = new List<(string, string, Item)>();
        var changed = new List<string>();
        foreach (var list in Lists(config))
        {
            var due = list.Closed.Where(i => i.ClosedOn is { } on && today.DayNumber - on.DayNumber > config.KeepDays)
                .OrderByDescending(i => i.ClosedOn).ToList();
            if (due.Count == 0)
                continue;
            foreach (var item in due)
                moved.Add((list.Name, item.ClosedOn!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture), item));
            if (dryRun)
                continue;
            foreach (var month in due.GroupBy(i => i.ClosedOn!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
                changed.Add(AddToArchive(config, month.Key, month.ToList()));
            var gone = due.SelectMany(i => Enumerable.Range(i.Start, i.End - i.Start)).ToHashSet();
            var kept = list.Lines.Where((_, n) => !gone.Contains(n)).ToList();
            var after = Read(Write(list.Path, kept), config);
            if (after.Closed.Count == 0)
            {
                var heading = after.Lines.FindIndex(l => l.StartsWith("## ", StringComparison.Ordinal)
                                                        && l[3..].Trim().Equals(config.ClosedHeading, StringComparison.OrdinalIgnoreCase));
                var end = heading + 1;
                while (end < after.Lines.Count && !after.Lines[end].StartsWith("## ", StringComparison.Ordinal) && after.Lines[end].Trim() != "---")
                    end++;
                if (!after.Lines.Skip(heading + 1).Take(end - heading - 1).Any(l => l.Trim() == "*None.*"))
                {
                    var lines = after.Lines.ToList();
                    lines.InsertRange(end, ["*None.*", ""]);
                    Write(list.Path, lines);
                }
            }
            changed.Add(list.Path);
        }
        return new ArchiveResult(moved, changed);
    }

    /// <summary>"two weeks" for 14, "10 days" for 10: how the archive's introduction says it.</summary>
    internal static string Span(int days) => days switch
    {
        1 => "a day",
        7 => "a week",
        14 or 21 or 28 => $"{(days == 14 ? "two" : days == 21 ? "three" : "four")} weeks",
        _ => $"{days} days",
    };

    private static string AddToArchive(Config config, string month, List<Item> items)
    {
        var path = System.IO.Path.Combine(config.ArchiveFolder, $"{month}.md");
        var name = $"{MonthNames[int.Parse(month[5..], CultureInfo.InvariantCulture) - 1]} {month[..4]}";
        List<string> head, rest;
        if (File.Exists(path))
        {
            var lines = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            var first = lines.FindIndex(l => l.StartsWith("- ", StringComparison.Ordinal));
            if (first < 0)
                first = lines.Count;
            (head, rest) = (lines[..first], lines[first..]);
        }
        else
        {
            head = [$"# Closed inbox items: {name}", "",
                $"*Items closed in {name}, newest first, word for word as they were. Moved here from the lists {Span(config.KeepDays)} after closing.*", ""];
            rest = [];
        }
        var body = string.Join("\n\n", items.Select(i => i.Text));
        var restText = string.Join("\n", rest).Trim('\n');
        var text = string.Join("\n", head).TrimEnd('\n') + "\n\n" + body + (restText.Trim().Length > 0 ? "\n\n" + restText : "") + "\n";
        Directory.CreateDirectory(config.ArchiveFolder);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Blanks();

    /// <summary>Writes a list's lines, keeping the file's own line endings (CRLF stays CRLF).</summary>
    internal static string Write(string path, List<string> lines)
    {
        var crlf = File.Exists(path) && File.ReadAllText(path).Contains("\r\n", StringComparison.Ordinal);
        var text = Blanks().Replace(string.Join("\n", lines), "\n\n").TrimEnd('\n') + "\n";
        File.WriteAllText(path, crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text, new UTF8Encoding(false));
        return path;
    }
}
