// md-inbox-background.exe: no window, ever. What the scheduled task runs (`archive`); its log is the settings' log_file.
return MdInbox.Cli.Run(args, new MdInbox.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
