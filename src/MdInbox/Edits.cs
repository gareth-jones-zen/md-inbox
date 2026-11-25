using System.Globalization;
using System.Text.RegularExpressions;

namespace MdInbox;

/// <summary>Adding, closing and re-dating items: each re-reads the list after every change, so it never works from
/// stale line numbers, and writes the file once.</summary>
public static partial class Inbox
{
    /// <summary>What an edit did: the list's file, the item as it now reads, and where it went.</summary>
    public sealed record Edited(string Path, string List, string Section, IReadOnlyList<string> Lines)
    {
        public string Text => string.Join("\n", Lines);
    }

    /// <summary>A section's stand-in when it has no items: "*None.*", "*Nothing open.*", "*Nothing yet.*".</summary>
    [GeneratedRegex(@"^\*(?:None|Nothing)\b[^*]*\*$", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^((?:\[[^\]\n]+\]\s*)+)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTags();

    /// <summary>The end of the headline: a full stop, question or exclamation mark followed by a space.</summary>
    [GeneratedRegex(@"[.?!](?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();

    // ---- add ------------------------------------------------------------------------------------------------------

    /// <summary>An item in the lists' shape from text: "[Tag] Headline. Detail" becomes
    /// "- **YYYY-MM-DD:** **[Tag] Headline.** Detail". Text that's already an item ("- ...") is kept as it is, and text
    /// that has its own bold only gets the date. Later lines (from a file) are indented under it.</summary>
    public static List<string> Format(string text, DateOnly raised)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            throw new UsageException("There's nothing to add: the text is empty.");
        var first = lines[0].Trim();
        string head;
        if (first.StartsWith("- ", StringComparison.Ordinal))
            head = first;
        else if (first.Contains("**", StringComparison.Ordinal))
            head = $"- **{raised:yyyy-MM-dd}:** {first}";
        else
        {
            var tags = LeadingTags().Match(first) is { Success: true } t ? t.Value.Trim() + " " : "";
            var rest = first[tags.TrimEnd().Length..].Trim();
            var end = SentenceEnd().Match(rest);
            var (headline, detail) = end.Success ? (rest[..(end.Index + 1)], rest[(end.Index + 1)..].Trim()) : (rest, "");
            if (headline.Length > 0 && !".?!".Contains(headline[^1], StringComparison.Ordinal))
                headline += ".";
            head = $"- **{raised:yyyy-MM-dd}:** **{tags}{headline}**" + (detail.Length > 0 ? " " + detail : "");
        }
        var item = new List<string> { head };
        item.AddRange(lines.Skip(1).Select(l => l.Trim().Length == 0 ? "" : char.IsWhiteSpace(l[0]) ? l : "  " + l));
        return item;
    }

    /// <summary>The first [tag] of some item lines (the same rule as Item.Tag).</summary>
    public static string? TagOf(IReadOnlyList<string> lines) => new Item("", "", 0, 0, lines.Count, lines, false).Tag;

    /// <summary>Which list and section an item goes in: a named list, else the one the settings' [tags] give for its
    /// tag; then --section (a few letters of the heading), else the tag's section (when the list is the tag's),
    /// else the list's only section. Anything less certain is refused, with the choices.</summary>
    public static (InboxList List, string Section) Place(Config config, string? listName, string? section, string? tag)
    {
        (string List, string? Section)? mapped = null;
        if (tag is not null)
        {
            var parts = TagParts.Of(tag);
            foreach (var key in new[] { tag, parts.ToString(), parts.Name })
                if (config.Tags.TryGetValue(key, out var found))
                {
                    mapped = found;
                    break;
                }
        }
        var list = listName is not null ? List(config, listName)
            : mapped is { } m ? List(config, m.List)
            : throw new UsageException(tag is null
                ? "Name the list (or start the text with a [tag] the settings' [tags] know)."
                : $"[{tag}] isn't in the settings' [tags], so name the list" +
                  (config.Tags.Count > 0 ? $" (tags known: {string.Join(", ", config.Tags.Keys.Order(StringComparer.OrdinalIgnoreCase))})." : "."));
        string Choices() => $"{list.Name}'s sections: {string.Join("; ", list.Sections)}";
        if (section is not null)
        {
            var exact = list.Sections.FirstOrDefault(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
            var some = list.Sections.Where(s => s.Contains(section, StringComparison.OrdinalIgnoreCase)).ToList();
            return exact is not null ? (list, exact)
                : some.Count == 1 ? (list, some[0])
                : throw new UsageException($"--section '{section}' matches {(some.Count == 0 ? "no section" : "more than one section")}. {Choices()}.");
        }
        if (mapped is { Section: { } mappedSection } && List(config, mapped.Value.List).Path == list.Path)
        {
            var exact = list.Sections.FirstOrDefault(s => s.Equals(mappedSection, StringComparison.OrdinalIgnoreCase));
            return exact is not null ? (list, exact)
                : throw new UsageException($"The settings' [tags] put [{tag}] in '{mappedSection}', which {list.Name} doesn't have. {Choices()}.");
        }
        return list.Sections.Count == 1 ? (list, list.Sections[0])
            : list.Sections.Count == 0 ? throw new UsageException($"{list.Name} has no sections to add to.")
            : throw new UsageException($"Which section? Use --section. {Choices()}.");
    }

    /// <summary>Puts item lines at the top of a section (an empty section loses its "*None.*"), and writes the file
    /// unless it's a dry run.</summary>
    public static Edited Add(Config config, InboxList list, string section, List<string> item, bool dryRun)
    {
        var lines = Insert(list, section, item, config);
        if (!dryRun)
            Write(list.Path, lines);
        return new Edited(list.Path, list.Name, section, item);
    }

    /// <summary>The list's lines with item lines at the top of a section.</summary>
    private static List<string> Insert(InboxList list, string section, IReadOnlyList<string> item, Config config)
    {
        var lines = list.Lines.ToList();
        var (heading, end) = Bounds(lines, section);
        if (heading < 0)
            throw new UsageException($"{list.Name} has no section '{section}'.");
        var items = (section.Equals(config.ClosedHeading, StringComparison.OrdinalIgnoreCase) ? list.Closed : list.Open)
            .Where(i => i.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count > 0)
        {
            // Spaced like the section's own items: a blank line between them, or none.
            var spaced = items.Count == 1 || Enumerable.Range(items[0].End, items[1].Start - items[0].End).Any(n => lines[n].Trim().Length == 0);
            lines.InsertRange(items[0].Start, spaced ? [.. item, ""] : item);
            return lines;
        }
        for (var n = end - 1; n > heading; n--)
            if (Placeholder().IsMatch(lines[n].Trim()))
            {
                lines.RemoveAt(n);
                end--;
            }
        var at = end;
        while (at > heading + 1 && lines[at - 1].Trim().Length == 0)
            at--;
        lines.InsertRange(at, ["", .. item, ""]);
        return lines;
    }

    /// <summary>A section's heading line and where it ends (the next heading, a "---", or the end), or (-1, -1).</summary>
    private static (int Heading, int End) Bounds(List<string> lines, string section)
    {
        var heading = lines.FindIndex(l => l.StartsWith("## ", StringComparison.Ordinal) && l[3..].Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        if (heading < 0)
            return (-1, -1);
        var end = heading + 1;
        while (end < lines.Count && !lines[end].StartsWith("## ", StringComparison.Ordinal) && lines[end].Trim() != "---")
            end++;
        return (heading, end);
    }

    /// <summary>An open item by its number in `list`.</summary>
    public static Item OpenItem(InboxList list, int number) =>
        number >= 1 && number <= list.Open.Count ? list.Open[number - 1]
            : throw new UsageException($"{list.Name} has {list.Open.Count} open item(s): there's no {number}.");

    // ---- close ----------------------------------------------------------------------------------------------------

    /// <summary>Moves an open item to the top of the list's closed section (made at the end if it has none), as
    /// "✔ **Closed (date): reason.** — " and the item word for word. A section left with no items says "*None.*".</summary>
    public static Edited Close(Config config, InboxList list, int number, string reason, DateOnly on, bool dryRun)
    {
        var item = OpenItem(list, number);
        reason = reason.Trim();
        if (reason.Length == 0)
            throw new UsageException("Say why it's closed: --reason \"...\".");
        if (!".?!".Contains(reason[^1], StringComparison.Ordinal))
            reason += ".";
        var closed = new List<string> { $"- ✔ **Closed ({on:yyyy-MM-dd}): {reason}** — {item.Lines[0].TrimStart()[2..]}" };
        closed.AddRange(item.Lines.Skip(1));

        var lines = Remove(config, list, item);
        if (Bounds(lines, config.ClosedHeading).Heading < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            lines.AddRange(["", $"## {config.ClosedHeading}", ""]);
        }
        lines = Insert(Parse(list.Path, lines, config), config.ClosedHeading, closed, config);
        if (!dryRun)
            Write(list.Path, lines);
        return new Edited(list.Path, list.Name, config.ClosedHeading, closed);
    }

    /// <summary>The list's lines without an item (and the blank lines after it). A section left with no open items says
    /// "*None.*".</summary>
    private static List<string> Remove(Config config, InboxList list, Item item)
    {
        var lines = list.Lines.ToList();
        var stop = item.End;
        while (stop < lines.Count && lines[stop].Trim().Length == 0 && stop > item.Start)
            stop++;
        lines.RemoveRange(item.Start, stop - item.Start);
        var after = Parse(list.Path, lines, config);
        if (!after.Open.Any(i => i.Section == item.Section))
        {
            var (heading, end) = Bounds(lines, item.Section);
            if (!lines.Skip(heading + 1).Take(end - heading - 1).Any(l => Placeholder().IsMatch(l.Trim())))
            {
                var at = end;
                while (at > heading + 1 && lines[at - 1].Trim().Length == 0)
                    at--;
                lines.InsertRange(at, ["", "*None.*", ""]);
            }
        }
        return lines;
    }

    /// <summary>The list's lines with item lines at the end of a section's open items (spaced like them); an empty
    /// section is the same as the top.</summary>
    private static List<string> InsertBottom(InboxList list, string section, IReadOnlyList<string> item, Config config)
    {
        var items = list.Open.Where(i => i.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count == 0)
            return Insert(list, section, item, config);
        var lines = list.Lines.ToList();
        var spaced = items.Count == 1 || Enumerable.Range(items[0].End, items[1].Start - items[0].End).Any(n => lines[n].Trim().Length == 0);
        lines.InsertRange(items[^1].End, spaced ? ["", .. item] : item);
        return lines;
    }

    // ---- move -----------------------------------------------------------------------------------------------------

    /// <summary>Moves an open item, word for word, to the top (or with bottom, the end) of a section in the same list or
    /// another. With a note, a dated line at the end of the item says so: "**23 Nov 2026: moved to In progress.** note".
    /// The section it leaves, if now empty, says "*None.*". Writes each list once, unless it's a dry run.</summary>
    public static (Edited From, Edited To) Move(Config config, InboxList from, int number, InboxList to, string section,
        bool bottom, string? note, DateOnly today, bool dryRun)
    {
        var item = OpenItem(from, number);
        if (section.Equals(config.ClosedHeading, StringComparison.OrdinalIgnoreCase))
            throw new UsageException($"'{section}' is where closed items go: use `close` to close it.");
        var moved = item.Lines.ToList();
        if (!string.IsNullOrWhiteSpace(note))
        {
            note = note.Trim();
            if (!".?!".Contains(note[^1], StringComparison.Ordinal))
                note += ".";
            moved.Add($"  - **{today.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}: moved to {section}.** {note}");
        }
        var same = string.Equals(Path.GetFullPath(from.Path), Path.GetFullPath(to.Path), StringComparison.OrdinalIgnoreCase);
        var left = Remove(config, from, item);
        var target = same ? Parse(from.Path, left, config) : to;
        var placed = bottom ? InsertBottom(target, section, moved, config) : Insert(target, section, moved, config);
        if (!dryRun)
        {
            if (!same)
                Write(from.Path, left);
            Write(to.Path, placed);
        }
        return (new Edited(from.Path, from.Name, item.Section, item.Lines), new Edited(to.Path, to.Name, section, moved));
    }

    // ---- redate ---------------------------------------------------------------------------------------------------

    /// <summary>Moves the date in an item's tag ("[Waiting: Ann Lee, 20 Nov 2026]") to a new one, and with a note adds
    /// a sub-bullet at the end of the item: "**18 Nov: chased in Teams.** Next: Fri 27 Nov."</summary>
    public static Edited Redate(Config config, InboxList list, int number, DateOnly when, string? note, DateOnly today, bool dryRun)
    {
        var item = OpenItem(list, number);
        if (item.Tag is not { } tag || item.Parts?.When is null)
            throw new UsageException($"{list.Name} {number}'s tag has no date to move" + (item.Tag is { } t ? $": [{t}]." : " (it has no tag)."));
        // The new date written as the old one was: "27 Nov 2026", or "2026-11-27".
        var iso = DateOnly.TryParseExact(tag[(tag.LastIndexOf(',') + 1)..].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var moved = $"{tag[..tag.LastIndexOf(',')]}, {when.ToString(iso ? "yyyy-MM-dd" : "d MMM yyyy", CultureInfo.InvariantCulture)}";
        var first = item.Lines[0];
        var at = first.IndexOf($"[{tag}]", StringComparison.Ordinal);
        var lines = item.Lines.ToList();
        lines[0] = first[..at] + $"[{moved}]" + first[(at + tag.Length + 2)..];
        if (!string.IsNullOrWhiteSpace(note))
        {
            note = note.Trim();
            if (!".?!".Contains(note[^1], StringComparison.Ordinal))
                note += ".";
            lines.Add($"  - **{today.ToString("d MMM", CultureInfo.InvariantCulture)}: {note}** Next: {when.ToString("ddd d MMM", CultureInfo.InvariantCulture)}.");
        }
        var all = list.Lines.ToList();
        all.RemoveRange(item.Start, item.End - item.Start);
        all.InsertRange(item.Start, lines);
        if (!dryRun)
            Write(list.Path, all);
        return new Edited(list.Path, list.Name, item.Section, lines);
    }

    /// <summary>A date as yyyy-mm-dd or "20 Nov 2026" (as a tag has it).</summary>
    public static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : LongDate(text.Trim());
}
