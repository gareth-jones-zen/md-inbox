# md-inbox

Lists of items kept in markdown files, read and kept from the command line: what's open, what's due, adding,
closing, reopening and re-dating items, and closed items moved to a monthly archive by themselves.

```
md-inbox list mine
md-inbox show mine 3
md-inbox due
md-inbox archive
```

```
mine
  Actions
       1. 2026-11-16  [Weekly update] Thank the team for the availability checker
       2. 2026-11-13  [Action] Speak to IS about sharing the contacts sheet
  Side projects
       3. 2026-11-17  [Personal] Build my own RSS reader
```

**The files stay yours.** md-inbox reads them as you write them, and the archive moves items word for word. It
doesn't add IDs, front matter or anything else you'd have to look past.

## The shape it expects

A folder of markdown files, one per list. In each:

- **`## ` headings are sections.** A top-level `- ` bullet in a section is an **item**, together with everything
  indented under it (sub-bullets, continued paragraphs). Other text in a section (an italic note, say) isn't an item.
- **A section can end at `---`** as well as at the next heading.
- **`## Recently closed`** (the name is a setting) holds closed items until the archive takes them.
- **An item's date** is the first `yyyy-mm-dd` on its first line; its **tag** the first `[Tag]` (a markdown link isn't
  one); its **title** the bold text holding the tag, or failing that the first bold text that isn't a date.
- **A closed item** starts `✔ **Closed (yyyy-mm-dd): what happened.** — ` in front of the original item. The archive
  goes by that date (or, failing that, the item's first date).
- **A dated action**, for `due`, is a line starting with a date (`- **Wed 25 Nov 2026: ...`), or an item whose tag ends
  in one: `[Action, 25 Nov 2026]`, or **`[Waiting: Ann Lee, 25 Nov 2026]`**, where the part after the colon says who.
  A tag's date can be `25 Nov 2026`, `25 November 2026` or `2026-11-25` (and `redate` writes the new one the same way).
  For a tag that names someone, `due` adds who and how long the item has been open: `(Ann Lee; raised 12 Nov, 6 days
  ago)`. So a list of things you're waiting on, each tagged with who and when to chase, comes up on the day; moving the
  chase on is changing the tag's date.

```markdown
# Mine

## Actions

- **2026-11-13:** **[Action] Speak to IS about sharing the contacts sheet.** Why it matters, and what's next.
  - **15 Nov: they want a demo first.**

## Recently closed

- ✔ **Closed (2026-11-12): done in the 11 Nov 1:1.** — **2026-11-06:** **[Action] Ask about the move.** Detail.
```

## Install

Windows (x64) only; nothing else is needed.

1. Download `md-inbox-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/md-inbox/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\md-inbox`.
3. Run `md-inbox config`, set `folder`, then `md-inbox autostart on` to archive by itself.

It holds two programs: `md-inbox.exe`, for a console, and `md-inbox-background.exe`, the same program with no
window, which the scheduled task runs.

## Commands

```
md-inbox list [LIST...]          the open items, numbered, under their sections (every list if none named)
    --section S                  only sections whose heading contains S
    --tag T                      only items whose tag contains T
    --closed                     the closed items waiting for the archive (numbered c1, c2...)
    --json                       as JSON, for another program (below)
md-inbox show LIST N             an item in full (N from list; cN for a closed one)
md-inbox due [--days N]          dated actions due within N days (default: due_ahead_days)
    --json                       as JSON, for another program (below)
md-inbox add [LIST] TEXT         add an item at the top of its section, dated today: "[Tag] Headline. Detail"
    --file F                     the item from a markdown file instead (later lines go under it)
    --section S                  the section (a few letters of its heading); else the tag's, from [tags]
    --date DATE                  the date it was raised (default today)
md-inbox close LIST N --reason R move an item to the closed section, "Closed (date): R." in front
    --date DATE                  the date it closed (default today)
md-inbox reopen LIST cN          put a closed item back as it was before it closed (cN from list --closed)
    --section S                  the section (a few letters of its heading); else the tag's, from [tags]; else the first
    --note T                     and add "**25 Nov 2026: reopened.** T" at the end of the item
md-inbox redate LIST N DATE      move the date in an item's tag ("[Waiting: Ann Lee, 20 Nov 2026]")
    --note T                     and add "**18 Nov: T.** Next: Fri 20 Nov." at the end of the item
md-inbox move LIST N --section S move an item, word for word, to the top of a section (a few letters of its heading)
    --to LIST                    in another list (default: the same one)
    --bottom                     to the end of the section instead (--top, the default)
    --note T                     and add "**23 Nov 2026: moved to <section>.** T" at the end of the item
    add, close, reopen, redate, move: --dry-run shows the result and changes nothing; --no-commit skips [git]
md-inbox archive [--dry-run]     move closed items past keep_days into the archive, and commit them
    --as-of DATE                 archive and due: as though today were DATE
md-inbox autostart on            the archive as [schedule] says (daily 06:30 and at logon): off, restart, status
md-inbox config                  create the settings file (if missing) and say where it is
```

**LIST** is a file's name without `.md`, or enough of it to be unique. Item numbers count a list's open items from
the top, across its sections, so they change when items are added or closed: take them from a fresh `list`.

**Exit codes:**

| Code | Means |
|---|---|
| 0 | Done. Also when the settings have `[git]` but git isn't installed, or the folder isn't a git repository (or is a folder inside one rather than its top): the list is saved, nothing is committed, and it says so in one line |
| 1 | Failed. For `archive`, also a commit the repository refused: the items are moved, and a rerun can't move them twice |
| 2 | Settings or arguments |
| 3 | `add`, `close`, `reopen`, `redate` or `move`: **saved in the list, but the repository refused the commit** (a hook, a lock, a conflict). Don't run it again, or the edit is made twice; commit the list's file by hand |

## JSON, for another program: `list --json` and `due --json`

The same items the plain commands print, for a program to read (a web page over the lists, say). Output is UTF-8,
whatever the console's code page, and always valid JSON: no items is an empty `items` array.

**`list --json [LIST...]`** takes `--section`, `--tag` and `--closed` as `list` does, and gives every list asked for
(every list, if none is named), even one with nothing in it:

```json
{
  "lists": [
    {
      "name": "waiting",
      "file": "inbox/waiting.md",
      "path": "C:\\Users\\ann\\notes\\inbox\\waiting.md",
      "sections": ["Waiting"],
      "items": [
        {
          "n": 1,
          "ref": "1",
          "section": "Waiting",
          "date": "2026-11-20",
          "tag": "Waiting: Cleo Marsh, 24 Nov 2026",
          "title": "The café floor plan",
          "due": "2026-11-24",
          "who": "Cleo Marsh",
          "closed": false,
          "closedOn": null,
          "text": "**2026-11-20:** **[Waiting: Cleo Marsh, 24 Nov 2026] The café floor plan.** Asked on Teams.",
          "subitems": ["**22 Nov: chased.** Next: Tue 24 Nov."],
          "markdown": "- **2026-11-20:** **[Waiting: Cleo Marsh, 24 Nov 2026] The café floor plan.** Asked on Teams.\n  - **22 Nov: chased.** Next: Tue 24 Nov.",
          "line": 9
        }
      ]
    }
  ]
}
```

| Field | Is |
|---|---|
| `name`, `file`, `path` | The list's name (as LIST takes it); its file relative to `[git]`'s `repo` (else to the folder holding the lists' folder), with `/`; and in full |
| `sections` | The list's sections, in order, empty ones too (not the closed one, nor `skip_sections`): what `add` and `move` can take |
| `n` | The item's number, as `list` gives it: `show`, `close`, `redate` and `move` take it. Like `list`'s, it changes as items are added and closed |
| `ref` | What `show` takes: `n`, or `c` and `n` for a closed item (`--closed`), which is what `reopen` takes |
| `date`, `tag`, `title` | As [above](#the-shape-it-expects); `null` when it has none |
| `due` | The date `due` goes by: the earliest of a dated tag and the item's lines that start with a date (`null` if none, and for a closed item) |
| `who` | Who the tag names (`[Waiting: Cleo Marsh, ...]`), else `null` |
| `closed`, `closedOn` | Whether it's a closed item, and the date it closed (else `null`) |
| `text` | Its first line, markdown, without the `- ` |
| `subitems` | Each sub-bullet's text, without its bullet. Anything under a sub-bullet (a deeper bullet, a continued line) joins it on a new line; text under the item before its first sub-bullet is only in `markdown` |
| `markdown` | The whole item, as it is in the file (with `\n` line endings) |
| `line` | The line its first line is on in the file, from 1 |

**`due --json [--days N]`** gives `today`, the window in `days`, and one entry in `items` for each line `due` prints,
soonest first: `list`, the item's fields as above (with `due` the date of *this* line, as an item can be due on two
lines), and

| Field | Is |
|---|---|
| `overdue`, `dueToday` | Whether this date is before today, or today |
| `raised` | When an item whose tag names someone was raised (its `date`), as `due` says it; else `null` |
| `dueText`, `dueLine` | The dated line (without its `- `) and its line in the file, from 1: the item's first line, or one under it |

```json
{
  "today": "2026-11-25",
  "days": 2,
  "items": [
    { "list": "waiting", "n": 1, "ref": "1", "section": "Waiting", "date": "2026-11-20",
      "tag": "Waiting: Cleo Marsh, 24 Nov 2026", "title": "The café floor plan", "due": "2026-11-24",
      "who": "Cleo Marsh", "closed": false, "closedOn": null, "text": "...", "subitems": ["..."], "markdown": "...",
      "line": 9, "overdue": true, "dueToday": false, "raised": "2026-11-20",
      "dueText": "**2026-11-20:** **[Waiting: Cleo Marsh, 24 Nov 2026] The café floor plan.** Asked on Teams.",
      "dueLine": 9 }
  ]
}
```

## Adding, closing, reopening, re-dating and moving

- **`add` writes the item in the lists' shape:** `[Tag] Headline. Detail` becomes
  `- **2026-11-18:** **[Tag] Headline.** Detail` (the headline is up to the first full stop, question or exclamation
  mark). Text that's already an item (`- ...`) goes in as it is; text with its own bold only gets the date.
- **Where it goes, and nothing is guessed:** the list you name, or the one `[tags]` gives for its tag (looked up by the
  whole tag, then without its date, then by its name: `[Waiting: Ann Lee, 20 Nov 2026]` finds `Waiting`). The section
  is `--section`, else the tag's, else the list's only one; otherwise it stops and lists the sections.
- **At the top of the section**, with a blank line after it if the section's items have them. An empty section's
  stand-in (`*None.*`, `*Nothing open.*`) goes.
- **`close`** moves the item, word for word, to the top of the closed section (made if the list has none), starting
  `✔ **Closed (date): reason.** — `. A section left with no items says `*None.*`.
- **`close` says which section the item came from** (`Closed mine 3 (from Actions):`), so it can be reopened there.
- **`reopen`** undoes a close: it takes the item out of the closed section, drops the `✔ **Closed (date): reason.** — `
  in front (or only a space after the bold, as some items closed by hand have it), and puts the rest back, sub-bullets
  and all, word for word, at the top of a section: `--section`, else the section `[tags]` gives its tag *in this list*,
  else the list's first section. It adds nothing unless asked, so an undone close leaves no trace; `--note` adds a
  dated line saying it was reopened and why. A closed section left empty says `*None.*`. It works on the items in
  `Recently closed` only (the refs `list --closed` gives): one already in the archive is copied back by hand, or added
  again. An item whose first line doesn't start with the `Closed` bold is refused, rather than guessed at.
- **`redate`** moves the date in the item's first tag, and with `--note` adds a dated line at the end of the item:
  a chase, recorded in the item itself.
- **`move`** takes an open item, every line of it, to another section of the same list or (with `--to`) another list:
  to the top, like `add`, or with `--bottom` the end, spaced like the section's items. The section it leaves says
  `*None.*` if it's now empty, and an empty section it goes to loses its stand-in. `--note` adds a dated line saying
  where it went and why. It won't move a closed item, or into the closed section (that's `close`).
- **N is from a fresh `list`**: numbers change as items are added and closed. Each command prints the item as it now
  reads, logs it, and with `[git]` commits that list's file (and only that; a `move` between lists commits both).
- **Line endings are kept:** a list with CRLF line endings stays CRLF. If the repository refuses the commit,
  the edit is still saved and it exits 3: don't run it again.

For example, closing an item by mistake and putting it back where it was:

```
md-inbox close mine 2 --reason "Sent"
Closed mine 2 (from Actions):

- ✔ **Closed (2026-11-25): Sent.** — **2026-11-13:** **[Action] Speak to IS about sharing the contacts sheet.**

md-inbox list mine --closed
mine
    c1. 2026-11-25  [Action] Speak to IS about sharing the contacts sheet

md-inbox reopen mine c1 --section Actions
Reopened mine c1 into mine / Actions:

- **2026-11-13:** **[Action] Speak to IS about sharing the contacts sheet.**
```

A program undoing a close (a web page over the lists, say) has the item's `section` from `list --json` before it
closes it, finds the closed copy in `list --closed --json` (its `ref`; it's `c1` straight after the close, as `close`
puts it at the top) and passes both to `reopen`.

## The archive

A closed item that closed more than `keep_days` ago moves, word for word, to `{archive_folder}/YYYY-MM.md` for the
month it closed, newest first: a new month's file gets a heading, an existing one gets the items at the top. A
`Recently closed` section left empty says `*None.*`. Nothing is deleted. With `[git]`, the files it changed (and only
those) are committed and, if asked, pushed.

## Sharing settings with other tools: `base`

Several tools built on the shared library can keep their common settings in one file, named at the top of each
tool's settings: `base = "base-config.toml"` (relative to the settings file). Then:

- **the base's `[paths]` are variables** for this tool's paths, looked up before the environment: with
  `EA_LOCAL = "%LOCALAPPDATA%/claude-ea"` there, `log_file = "%EA_LOCAL%/md-inbox.log"` works here;
- **its other settings that md-inbox knows and this file doesn't set are used** (a base's sign-in settings, which
  md-inbox doesn't have, are left out);
- **a table such as `[git]` applies only if this file has that section too**, even empty, its own keys winning.

The loader is background-tools-lib's `PyCompat.BaseSettings`.

## Settings

`%APPDATA%\md-inbox\config.toml` by default (`--config` or `MD_INBOX_CONFIG` for another).

```toml
base = "base-config.toml"   # optional: settings shared with other tools
folder = "inbox"                    # the lists: every *.md directly in it (relative to this file)
closed_heading = "Recently closed"
keep_days = 14
archive_folder = "closed"           # relative to folder
skip_sections = ["How to use"]      # sections that hold no items (an index, conventions)
due_ahead_days = 2
log_file = "..."                    # default %LOCALAPPDATA%\md-inbox\md-inbox.log

[schedule]                          # optional: when autostart runs the archive (below)
daily_at = "06:30"

[tags]                              # optional: where `add` puts a tagged item: "list" or "list / section"
"Ann 1:1" = "one-to-ones / Ann Lee"
Waiting = "waiting / Waiting"

[git]                               # optional: commit (and push) what the archive and edits change
repo = "."                          # the repository's top
push = true                         # a branch with no upstream isn't pushed (said once)
trailers = ["Co-Authored-By: ..."]
```

**git is optional.** With no `[git]`, nothing is committed. With one, but no git installed, or a `repo` that isn't a
git repository (or is a folder inside one rather than its top, so nothing is committed into a repository the folder
only sits in), the lists are still saved: it says "git: not committing: ..." once and exits 0.

The file is read strictly: an unknown setting, or one of the wrong type, is an error that names it.

### `[schedule]`

The scheduling settings every tool on background-tools-lib shares (its README, "[schedule]: the shared scheduling
settings", has every key). md-inbox's task runs the archive daily at 06:30 and at logon by default:

```toml
[schedule]
daily_at = "06:30"          # or every_minutes = N instead
at_logon = true             # the default
after_unlock_minutes = 0    # the default: no run after an unlock
end_date = "2027-01-01"     # optional: from this date a scheduled run does nothing
```

`hours`, `days` and `end_date` are kept by the task's own runs (`archive --scheduled`); an `archive` run by hand always
runs. The trigger keys are fixed when the task is registered: after changing them, run `md-inbox autostart on` again
(`autostart status` says when it's needed). `daily_at` at the top level, the old name, is still read, with a line in
the log; setting it alongside `[schedule]` is an error.

## Building

.NET 11 (see `global.json`), with the shared libraries from
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) checked out beside this repo:

```
dotnet test -c Release
dotnet publish src/MdInbox.Console -c Release -r win-x64 -o out    # Native AOT
```

A tag `vX.Y.Z` builds a release (`.github/workflows/release.yml`), against the library commit pinned there.
