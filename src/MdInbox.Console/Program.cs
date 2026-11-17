// md-inbox.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return MdInbox.Cli.Run(args, new MdInbox.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });
