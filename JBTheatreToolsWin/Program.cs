namespace JBTheatreTools;

internal static class Program
{
    // Must mirror the commands routed in Cli.Run — otherwise an unlisted command (e.g. --uninstall)
    // falls through and silently launches the GUI instead of running headless.
    private static readonly HashSet<string> CliCommands =
        new() { "--list", "--installed", "--releases", "--install", "--uninstall",
                "--launch", "--self-check", "--help", "-h" };

    [STAThread]
    private static int Main(string[] args)
    {
        // A recognised `--…` command runs the headless CLI and exits; otherwise the GUI launches.
        if (args.Length > 0 && CliCommands.Contains(args[0]))
            return Cli.Run(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
