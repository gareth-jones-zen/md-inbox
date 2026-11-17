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

    /// <summary>The first yyyy-mm-dd on its (original) first line: when it was raised. For a closed item, see ClosedOn.</summary>
    public DateOnly? Date => FirstDate(Head);

    /// <summary>When a closed item closed: the first "Closed (yyyy-mm-dd)", else its first date.</summary>
    public DateOnly? ClosedOn =>
        ClosedDate().Match(Text) is { Success: true } m ? Parse(m.Groups[1].Value) : FirstDate(Text);

    /// <summary>The first [Tag] on its (original) first line (not a markdown link's text).</summary>
    public string? Tag => TagPattern().Match(Head) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

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

/// <summary>A dated action that's due: an item line starting with a date, such as "- **Wed 25 Nov 2026: ..." or
/// "- **[Action, 25 Nov 2026]".</summary>
public sealed record Due(DateOnly When, Item Item, string Line);

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

    public static InboxList Read(string path, Config config)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var lines = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
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

    /// <summary>Open items with a line starting with a date on or before today + ahead days, soonest first.</summary>
    public static List<Due> DueItems(IEnumerable<InboxList> lists, DateOnly today, int ahead)
    {
        var found = new List<Due>();
        foreach (var item in lists.SelectMany(l => l.Open))
        {
            foreach (var line in item.Lines)
            {
                if (Dated().Match(line) is not { Success: true } m)
                    continue;
                var text = m.Groups[1].Value;
                var formats = new[] { "d MMM yyyy", "d MMMM yyyy" };
                if (!DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                    continue;
                if (when <= today.AddDays(ahead))
                    found.Add(new Due(when, item, line.Trim()));
            }
        }
        return found.OrderBy(d => d.When).ThenBy(d => d.Item.List).ToList();
    }

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

    private static string Write(string path, List<string> lines)
    {
        File.WriteAllText(path, Blanks().Replace(string.Join("\n", lines), "\n\n"), new UTF8Encoding(false));
        return path;
    }
}
