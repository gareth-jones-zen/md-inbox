using System.Text.RegularExpressions;
using PyCompat;
using RepoCommit;
using Tomlyn;
using Tomlyn.Model;

namespace MdInbox;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>The settings file.
///
///     folder = "inbox"                    # the lists: every *.md directly in it; relative to this file
///     closed_heading = "Recently closed"  # the section closed items wait in
///     keep_days = 14                      # how long they wait before the archive
///     archive_folder = "closed"           # where they go, as YYYY-MM.md; relative to folder
///     skip_sections = ["How to use"]      # "## " sections that hold no items (conventions, an index)
///     due_ahead_days = 2                  # `due`: dated items up to this many days ahead
///     daily_at = "06:30"                  # autostart: the archive runs daily at this time, and at logon
///     log_file = "..."                    # default %LOCALAPPDATA%\md-inbox\md-inbox.log
///
///     [git]                               # optional: commit (and push) what the archive moves
///     repo = "."
///     push = true
///     trailers = ["Co-Authored-By: ..."]
///
/// The file is read strictly: an unknown key or a value of the wrong type is an error that names it.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "MD_INBOX_CONFIG";

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# md-inbox settings. See the README.\n" +
        "# base = \"base-config.toml\"          # settings shared with other tools\n" +
        "folder = \"inbox\"                    # the lists: every *.md directly in it (relative to this file)\n" +
        "# closed_heading = \"Recently closed\"\n" +
        "# keep_days = 14\n" +
        "# archive_folder = \"closed\"          # relative to folder\n" +
        "# skip_sections = [\"How to use\"]\n" +
        "# due_ahead_days = 2\n" +
        "# daily_at = \"06:30\"\n" +
        "\n" +
        "# [git]                             # commit (and push) what the archive moves\n" +
        "# repo = \".\"\n" +
        "# push = true\n";

    /// <summary>Every top-level setting: the strict check's list, and what a base file may supply.</summary>
    public static readonly string[] KnownKeys =
        ["folder", "closed_heading", "keep_days", "archive_folder", "skip_sections", "due_ahead_days", "daily_at", "log_file", "git"];

    private static readonly string[] TopKeys = KnownKeys;

    public required string Path { get; init; }
    public required string Folder { get; init; }
    public string ClosedHeading { get; init; } = "Recently closed";
    public int KeepDays { get; init; } = 14;
    public required string ArchiveFolder { get; init; }
    public IReadOnlyList<string> SkipSections { get; init; } = [];
    public int DueAheadDays { get; init; } = 2;
    public string DailyAt { get; init; } = "06:30";
    public required string LogFile { get; init; }
    public GitSettings? Git { get; init; }

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "md-inbox"));
    }

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "md-inbox", "config.toml"));
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d\z", RegexOptions.CultureInvariant)]
    private static partial Regex Clock();

    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `md-inbox config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;   // the base's [paths], then the environment
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)   // the base file: the message names it
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        foreach (var key in raw.Keys)
            if (!TopKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", TopKeys)}).");

        var dir = System.IO.Path.GetDirectoryName(full)!;
        string PathFrom(string value, string against)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
            return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(against, expanded);
        }
        string? Text(string key) => raw.TryGetValue(key, out var value)
            ? value as string ?? throw new UsageException($"{full}: {key} should be a string.")
            : null;
        int Number(string key, int fallback, int min)
        {
            if (!raw.TryGetValue(key, out var value))
                return fallback;
            if (value is not long n || n < min || n > 3650)
                throw new UsageException($"{full}: {key} should be a whole number, {min} or more.");
            return (int)n;
        }

        var folder = Text("folder") is { Length: > 0 } f ? PathFrom(f, dir)
            : throw new UsageException($"{full}: folder is needed: the folder of lists (relative to this file).");
        var skip = new List<string>();
        if (raw.TryGetValue("skip_sections", out var s))
        {
            if (s is not TomlArray array || array.Any(x => x is not string))
                throw new UsageException($"{full}: skip_sections should be a list of section names.");
            skip.AddRange(array.Select(x => ((string)x!).Trim()));
        }
        var daily = Text("daily_at") ?? "06:30";
        if (!Clock().IsMatch(daily))
            throw new UsageException($"{full}: daily_at should be a time such as \"06:30\".");
        GitSettings? git = null;
        if (raw.TryGetValue("git", out var g))
        {
            try
            {
                git = GitSettings.Read(g, full, value => PathFrom(value, dir));
            }
            catch (FormatException error)
            {
                throw new UsageException(error.Message);
            }
        }

        return new Config
        {
            Path = full,
            Folder = folder,
            ClosedHeading = Text("closed_heading") is { Length: > 0 } h ? h : "Recently closed",
            KeepDays = Number("keep_days", 14, 0),
            ArchiveFolder = PathFrom(Text("archive_folder") is { Length: > 0 } a ? a : "closed", folder),
            SkipSections = skip,
            DueAheadDays = Number("due_ahead_days", 2, 0),
            DailyAt = daily,
            LogFile = Text("log_file") is { Length: > 0 } log ? PathFrom(log, dir) : PyPath.Pure(System.IO.Path.Combine(Local(env), "md-inbox.log")),
            Git = git,
        };
    }

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
