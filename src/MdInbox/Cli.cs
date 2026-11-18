using System.Globalization;
using System.Reflection;
using System.Text;
using WinBackground;

namespace MdInbox;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<DateOnly> Today { get; init; } = () => DateOnly.FromDateTime(DateTime.Now);
    public Func<ITask> Task { get; init; } = () => new ScheduledTask(Cli.TaskName, "Moves closed inbox items to the monthly archive (md-inbox archive).");
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
}

/// <summary>md-inbox: lists of items in markdown files. Exit codes: 0 done; 1 failed; 2 settings or arguments.</summary>
public static class Cli
{
    public const string Prog = "md-inbox";
    public const string TaskName = "md-inbox archive";
    public const string BackgroundExe = "md-inbox-background.exe";

    private const string Usage =
        "usage: md-inbox [-h] [--version] [--config CONFIG] {config,list,show,due,archive,autostart} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "md-inbox: lists of items kept in markdown files: what's open, what's due, and closed items archived by month.\n\n" +
        "    md-inbox list [LIST...]          the open items, numbered, under their sections (all lists if none named)\n" +
        "        --section S                  only sections whose heading contains S\n" +
        "        --tag T                      only items whose [tag] contains T\n" +
        "        --closed                     the closed items waiting for the archive instead\n" +
        "    md-inbox show LIST N             an item in full (N from `list`; cN for a closed one)\n" +
        "    md-inbox due [--days N]          dated items due within N days (default: the settings' due_ahead_days)\n" +
        "    md-inbox archive [--dry-run]     move closed items past keep_days to the archive, and commit them\n" +
        "    md-inbox autostart on            run the archive daily and at logon (off, restart, status)\n" +
        "    md-inbox config                  create the settings file (if missing) and say where it is\n\n" +
        "  LIST is a file's name without .md, or enough of it to be unique.\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\md-inbox\\config.toml)\n";

    private static readonly string[] Commands = ["config", "list", "show", "due", "archive", "autostart"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config, Section, Tag;
        public string Command = "";
        public List<string> Args = [];
        public bool Closed, DryRun;
        public int? Days;
        public DateOnly? AsOf;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }

        try
        {
            if (options.Command == "config")
            {
                host.Out.WriteLine(MdInbox.Config.CreateDefault(options.Config ?? host.Env(MdInbox.Config.EnvConfig), host.Env));
                return 0;
            }
            var config = MdInbox.Config.Load(options.Config, host.Env);
            return options.Command switch
            {
                "list" => List(config, options, host),
                "show" => Show(config, options, host),
                "due" => DueCommand(config, options, host),
                "archive" => ArchiveCommand(config, options, host),
                _ => Autostart(config, options.Args[0], host),
            };
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int List(Config config, Options options, CliHost host)
    {
        var lists = options.Args.Count == 0 ? Inbox.Lists(config) : options.Args.Select(a => Inbox.List(config, a)).ToList();
        var text = new StringBuilder();
        foreach (var list in lists)
        {
            var items = (options.Closed ? list.Closed : list.Open)
                .Where(i => options.Section is null || i.Section.Contains(options.Section, StringComparison.OrdinalIgnoreCase))
                .Where(i => options.Tag is null || (i.Tag?.Contains(options.Tag, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
            if (items.Count == 0 && options.Args.Count == 0)
                continue;
            text.Append(text.Length > 0 ? "\n" : "").Append(list.Name).Append(items.Count == 0 ? ": nothing\n" : "\n");
            foreach (var section in items.GroupBy(i => i.Section))
            {
                if (!options.Closed)
                    text.Append("  ").Append(section.Key).Append('\n');
                foreach (var item in section)
                {
                    var number = (options.Closed ? "c" : "") + item.Number.ToString(CultureInfo.InvariantCulture);
                    var date = (options.Closed ? item.ClosedOn : item.Date) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "          ";
                    var tag = item.Tag is { } t ? $"[{t}] " : "";
                    text.Append($"  {(options.Closed ? "" : "  ")}{number,4}. {date}  {tag}{item.Title}\n");
                }
            }
        }
        host.Out.Write(text.Length > 0 ? text.ToString() : "Nothing open.\n");
        return 0;
    }

    private static int Show(Config config, Options options, CliHost host)
    {
        var list = Inbox.List(config, options.Args[0]);
        var which = options.Args[1];
        var closed = which.StartsWith('c');
        if (!int.TryParse(closed ? which[1..] : which, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new UsageException($"'{which}' isn't an item number (as `list` gives them: 3, or c2 for a closed one).");
        var items = closed ? list.Closed : list.Open;
        var item = items.FirstOrDefault(i => i.Number == n)
                   ?? throw new UsageException($"{list.Name} has {items.Count} {(closed ? "closed" : "open")} item(s): there's no {which}.");
        host.Out.Write($"{list.Name} / {item.Section}\n\n{item.Text}\n");
        return 0;
    }

    private static int DueCommand(Config config, Options options, CliHost host)
    {
        var today = options.AsOf ?? host.Today();
        var due = Inbox.DueItems(Inbox.Lists(config), today, options.Days ?? config.DueAheadDays);
        if (due.Count == 0)
            host.Out.WriteLine("Nothing due.");
        foreach (var d in due)
        {
            var when = d.When < today ? "overdue " : d.When == today ? "today   " : "        ";
            var parts = d.Item.Parts;
            var tag = parts is null ? "" : parts.When is null ? $"[{parts}] " : $"[{parts.Name}] ";
            var who = parts?.Who is { } name
                ? $"  ({name}; {(d.Item.Date is { } raised ? $"raised {raised:d MMM}, {Age(today.DayNumber - raised.DayNumber)}" : "no date raised")})"
                : "";
            host.Out.WriteLine($"{d.When:ddd dd MMM}  {when}{d.Item.List} {d.Item.Number}: {tag}{d.Item.Title}{who}");
        }
        return 0;
    }

    private static string Age(int days) => days switch
    {
        <= 0 => "today",
        1 => "1 day ago",
        _ => $"{days} days ago",
    };

    private static int ArchiveCommand(Config config, Options options, CliHost host)
    {
        var log = Logger.Create(config.LogFile, host.Out, stampEcho: false);
        var result = Inbox.Archive(config, options.AsOf ?? host.Today(), options.DryRun);
        foreach (var (list, month, item) in result.Moved)
            log($"{(options.DryRun ? "would move" : "moved")}  {list} -> {month}  {item.Title}");
        log($"archive: {result.Moved.Count} closed item(s) {(options.DryRun ? "due" : "moved")}");
        if (options.DryRun || result.Changed.Count == 0 || config.Git is not { Commit: true } git)
            return 0;
        var committer = git.Open(log);
        var done = committer.Commit($"Inbox: archive {result.Moved.Count} closed item(s) older than {config.KeepDays} days",
            result.Changed, trailers: git.Trailers, push: git.Push);
        if (done.Error is { } error)
        {
            log($"commit failed: {error}");
            return 1;
        }
        log(done.Committed ? $"committed {done.Commit}{(git.Push ? done.Pushed ? ", pushed" : $", push failed: {done.PushError}" : "")}" : "nothing to commit");
        return 0;
    }

    private static int Autostart(Config config, string action, CliHost host)
    {
        var task = host.Task();
        switch (action)
        {
            case "on":
                var background = Path.Combine(host.ProgramDir, BackgroundExe);
                var exe = File.Exists(background) ? background : Path.Combine(host.ProgramDir, "md-inbox.exe");
                var line = task.Enable(exe, ["--config", config.Path, "archive"], Path.GetDirectoryName(config.Path),
                    new TaskSchedule { AtLogon = true, DailyAt = config.DailyAt, KeepRunning = false, TimeLimit = TimeSpan.FromMinutes(10) });
                host.Out.WriteLine($"Autostart is on: '{TaskName}' runs the archive daily at {config.DailyAt} and at logon, and has run now.\n  {line}");
                return 0;
            case "off":
                host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
                return 0;
            case "restart":
                try
                {
                    host.Out.WriteLine(task.Restart() ? $"Started '{TaskName}' now." : "Autostart is off, so there's nothing to restart.");
                }
                catch (InvalidOperationException error)
                {
                    host.Err.WriteLine(error.Message);
                    return 1;
                }
                return 0;
            default:
                var info = task.Status();
                if (info is null)
                    host.Out.WriteLine("Autostart is off. `md-inbox autostart on` turns it on.");
                else
                {
                    host.Out.WriteLine($"Autostart is on ('{TaskName}').");
                    foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        host.Out.WriteLine($"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
                }
                return 0;
        }
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --config: expected one argument");
                options.Config = argv[i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else
                throw new ArgumentsException($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new ArgumentsException("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands.Select(c => $"'{c}'"))})");

        string Value(string name, ref int at, string arg)
        {
            if (arg.StartsWith(name + "=", StringComparison.Ordinal))
                return arg[(name.Length + 1)..];
            if (++at >= argv.Count)
                throw new ArgumentsException($"argument {name}: expected one argument");
            return argv[at];
        }

        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            var name = arg.Split('=', 2)[0];
            switch (options.Command, name)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
                case ("list", "--section"):
                    options.Section = Value(name, ref i, arg);
                    break;
                case ("list", "--tag"):
                    options.Tag = Value(name, ref i, arg);
                    break;
                case ("list", "--closed"):
                    options.Closed = true;
                    break;
                case ("archive", "--dry-run"):
                    options.DryRun = true;
                    break;
                case ("archive" or "due", "--as-of"):
                    var day = Value(name, ref i, arg);
                    if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
                        throw new ArgumentsException($"argument --as-of: expected a date (yyyy-mm-dd), not '{day}'");
                    options.AsOf = asOf;
                    break;
                case ("due", "--days"):
                    var text = Value(name, ref i, arg);
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days > 3650)
                        throw new ArgumentsException($"argument --days: expected a number of days, not '{text}'");
                    options.Days = days;
                    break;
                default:
                    if (arg.StartsWith('-') && arg != "-")
                        throw new ArgumentsException($"unrecognized arguments: {arg}");
                    options.Args.Add(arg);
                    break;
            }
        }
        var (min, max, what) = options.Command switch
        {
            "show" => (2, 2, "list, number"),
            "autostart" => (1, 1, "action"),
            "list" => (0, 50, ""),
            _ => (0, 0, ""),
        };
        if (options.Args.Count < min)
            throw new ArgumentsException($"the following arguments are required: {what}");
        if (options.Args.Count > max)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.Args.Skip(max))}");
        if (options.Command == "autostart" && options.Args[0] is not ("on" or "off" or "restart" or "status"))
            throw new ArgumentsException($"argument action: invalid choice: '{options.Args[0]}' (choose from 'on', 'off', 'restart', 'status')");
        return options;
    }
}
