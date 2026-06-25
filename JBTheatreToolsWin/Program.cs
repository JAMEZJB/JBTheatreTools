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
        bool cliInvocation = args.Any(a => Cli.Commands.Contains(a))
                             || args.Any(a => a.StartsWith('-'));
        if (cliInvocation)
            return Cli.Run(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
