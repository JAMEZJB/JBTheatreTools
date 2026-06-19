namespace JBTheatreTools;

internal static class Program
{
    private static readonly HashSet<string> CliCommands =
        new() { "--list", "--installed", "--install", "--launch", "--help", "-h" };

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
