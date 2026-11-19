namespace JBTheatreTools;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Route to the headless CLI when invoked with a recognised verb ANYWHERE in the args, or with
        // any `-`/`--` option (so `--token X --install helo` and typo'd flags hit the CLI/usage error
        // instead of silently opening the GUI on a headless box — THEATRE-01). A double-clicked GUI exe
        // gets no args; OS restart-manager args use `/`, so they still fall through to the GUI.
        // `--trigger-host` (Stagehand's hidden copy for Dashboard buttons) comes first: only the web trigger, headless —
        // never a window or a tray icon.
        StartupLog.Init();
        switch (TriggerRegistration.ModeOf(args, Cli.Commands))
        {
            case LaunchMode.TriggerHost:
                StartupLog.Header("background web trigger");
                return TriggerHostMain.Run();
            case LaunchMode.Cli:
                StartupLog.Header("command line", LogFile.MaskedArguments(args));
                return Cli.Run(args).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        HouseFonts.Load();   // Inter, once, before the first window measures any text
        Application.Run(new MainForm());
        return 0;
    }
}
