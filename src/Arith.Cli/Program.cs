using System.CommandLine;

namespace Arith.Cli;

internal static class Program
{
    internal static int Main(string[] args) =>
        BuildRootCommand().Parse(args).Invoke();

    internal static RootCommand BuildRootCommand()
    {
        RootCommand rootCommand = new("The Arith compiler.");

        Command versionCommand = new("version")
        {
            Description = "Print the arith version and exit.",
        };
        versionCommand.SetAction(parseResult =>
            parseResult.InvocationConfiguration.Output.WriteLine(VersionInfo.DisplayVersion));
        rootCommand.Subcommands.Add(versionCommand);

        return rootCommand;
    }
}
