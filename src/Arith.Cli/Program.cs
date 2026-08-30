using System.CommandLine;

using Arith.Cli.Experiments;

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
        rootCommand.Subcommands.Add(BuildExperimentCommand());

        return rootCommand;
    }

    private static Command BuildExperimentCommand()
    {
        Command experimentCommand = new("experiment")
        {
            Description = "Experimental commands used to prototype compiler stages.",
        };

        Argument<string> outputDirectoryArgument = new("output-directory")
        {
            Description = "Directory the fib program is written into (created if missing).",
        };
        Command buildFibCommand = new("build-fib-command")
        {
            Description = "Emit a demo `fib` console program by generating .NET IL and metadata directly.",
        };
        buildFibCommand.Arguments.Add(outputDirectoryArgument);
        buildFibCommand.SetAction(parseResult =>
        {
            string outputDirectory = parseResult.GetRequiredValue(outputDirectoryArgument);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            foreach (string path in FibCommandEmitter.Emit(outputDirectory))
            {
                output.WriteLine($"wrote {path}");
            }
        });
        experimentCommand.Subcommands.Add(buildFibCommand);

        return experimentCommand;
    }
}
