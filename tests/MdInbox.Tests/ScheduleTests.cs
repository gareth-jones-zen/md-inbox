using WinBackground;

namespace MdInbox.Tests;

/// <summary>[schedule], the shared scheduling settings (background-tools-lib): a job, the archive daily at 06:30 and at
/// logon by default. daily_at at the top level is its old name, still read.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "md-inbox-schedule-" + Guid.NewGuid().ToString("N"));
    private const string Settings = "folder = \"inbox\"\nlog_file = \"md-inbox.log\"\n";

    public ScheduleTests() => Directory.CreateDirectory(Path.Combine(dir, "inbox"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private Config Load(string extra)
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + extra);
        return Config.Load(Path.Combine(dir, "config.toml"), _ => null);
    }

    private sealed class FakeTask : ITask
    {
        public string Name => Cli.TaskName;
        public TaskSchedule? Schedule { get; private set; }
        public List<string> Args { get; } = [];
        public IReadOnlyDictionary<string, string>? State { get; set; }
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => throw new NotSupportedException();
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Schedule = schedule;
            Args.AddRange(args);
            return exe;
        }
        public bool Disable() => true;
        public bool Stop() => true;
        public IReadOnlyDictionary<string, string>? Status() => State;
    }

    private (int Code, string Out, string Err) Run(FakeTask task, DateTime now, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args],
            new CliHost { Out = output, Err = errors, Env = _ => null, Today = () => DateOnly.FromDateTime(now), Now = () => now, Task = () => task });
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public void With_no_schedule_the_archive_runs_daily_at_0630_and_at_logon_as_before()
    {
        var config = Load("");
        Assert.Equal("06:30", config.Schedule.DailyAt);
        Assert.Equal("06:30", config.DailyAt);
        Assert.Empty(config.Deprecations);
        var task = new FakeTask();
        var (code, output, _) = Run(task, new DateTime(2026, 11, 23, 9, 0, 0), "autostart", "on");
        Assert.Equal(0, code);
        Assert.Contains("runs the archive daily at 06:30 and at logon", output);
        Assert.Equal("06:30", task.Schedule!.DailyAt);
        Assert.True(task.Schedule.AtLogon && task.Schedule.Every is null && task.Schedule.AfterUnlock is null && !task.Schedule.KeepRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), task.Schedule.TimeLimit);
        Assert.Equal(["--config", Path.Combine(dir, "config.toml"), "archive", "--scheduled"], task.Args);
        Assert.Equal("v1 job every=0 daily=06:30 logon=yes unlock=0", task.Schedule.Fingerprint);
    }

    [Fact]
    public void Schedule_is_read_and_the_old_name_still_works()
    {
        Assert.Equal("07:15", Load("[schedule]\ndaily_at = \"07:15\"\n").Schedule.DailyAt);
        var old = Load("daily_at = \"05:45\"\n");
        Assert.Equal("05:45", old.Schedule.DailyAt);
        Assert.Contains("daily_at", Assert.Single(old.Deprecations));
    }

    [Fact]
    public void Both_forms_or_an_unknown_key_is_an_error()
    {
        Assert.Contains("both set", Assert.Throws<UsageException>(() => Load("daily_at = \"05:45\"\n[schedule]\ndaily_at = \"06:00\"\n")).Message);
        Assert.Contains("[schedule]", Assert.Throws<UsageException>(() => Load("[schedule]\ndaily = \"06:00\"\n")).Message);
    }

    [Fact]
    public void A_scheduled_archive_keeps_to_the_schedule_and_one_by_hand_always_runs()
    {
        Load("[schedule]\ndaily_at = \"06:30\"\nend_date = \"2026-11-01\"\n");
        var task = new FakeTask();
        Assert.Contains("archive: not run", Run(task, new DateTime(2026, 11, 23, 6, 30, 0), "archive", "--scheduled").Out);
        Assert.Contains("archive: 0 closed item(s) moved", Run(task, new DateTime(2026, 11, 23, 6, 30, 0), "archive").Out);
    }

    [Fact]
    public void Status_says_when_the_task_needs_registering_again()
    {
        Load("");
        var task = new FakeTask { State = new Dictionary<string, string> { ["state"] = "Ready", ["description"] = "x (schedule: v1 job every=0 daily=06:30 logon=yes unlock=0)" } };
        Assert.DoesNotContain("autostart on", Run(task, DateTime.Now, "autostart", "status").Out);
        task.State = new Dictionary<string, string> { ["state"] = "Ready", ["description"] = "x (schedule: v1 job every=0 daily=07:00 logon=yes unlock=0)" };
        Assert.Contains("settings changed since this task was registered: run `md-inbox autostart on`", Run(task, DateTime.Now, "autostart", "status").Out);
        task.State = new Dictionary<string, string> { ["state"] = "Ready" };
        Assert.Contains("registered before its schedule was recorded", Run(task, DateTime.Now, "autostart", "status").Out);
    }
}
