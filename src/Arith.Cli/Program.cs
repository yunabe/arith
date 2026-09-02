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
        rootCommand.Subcommands.Add(BuildBuildCommand());
        rootCommand.Subcommands.Add(BuildRunCommand());
        rootCommand.Subcommands.Add(BuildExperimentCommand());

        return rootCommand;
    }

    private static Command BuildBuildCommand()
    {
        Argument<string> sourceArgument = new("source")
        {
            Description = "The .arith source file to compile.",
        };
        Option<string?> outputOption = new("--output", "-o")
        {
            Description = "Directory the program is written into (default: the current directory).",
        };
        Command buildCommand = new("build")
        {
            Description = "Compile an Arith source file into a .NET assembly.",
        };
        buildCommand.Arguments.Add(sourceArgument);
        buildCommand.Options.Add(outputOption);
        buildCommand.SetAction(parseResult => CompilerCommands.Build(
            parseResult.GetRequiredValue(sourceArgument),
            parseResult.GetValue(outputOption),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error));
        return buildCommand;
    }

    private static Command BuildRunCommand()
    {
        Argument<string> sourceArgument = new("source")
        {
            Description = "The .arith source file to compile and run.",
        };
        Command runCommand = new("run")
        {
            Description = "Compile and run an Arith source file, forwarding its exit code.",
        };
        runCommand.Arguments.Add(sourceArgument);
        runCommand.SetAction(parseResult => CompilerCommands.Run(
            parseResult.GetRequiredValue(sourceArgument),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error));
        return runCommand;
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
        Option<bool> aotOption = new("--aot")
        {
            Description = "Compile the program ahead-of-time into a single native executable "
                + "(requires the platform's native linker; no dotnet host needed to run it).",
        };
        Command buildFibCommand = new("build-fib-command")
        {
            Description = "Emit a demo `fib` console program by generating .NET IL and metadata directly.",
        };
        buildFibCommand.Arguments.Add(outputDirectoryArgument);
        buildFibCommand.Options.Add(aotOption);
        buildFibCommand.SetAction(parseResult =>
        {
            string outputDirectory = parseResult.GetRequiredValue(outputDirectoryArgument);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            if (parseResult.GetValue(aotOption))
            {
                string executable = FibCommandEmitter.EmitNativeExecutable(outputDirectory, output);
                output.WriteLine($"wrote {executable}");
                return;
            }

            foreach (string path in FibCommandEmitter.Emit(outputDirectory))
            {
                output.WriteLine($"wrote {path}");
            }
        });
        experimentCommand.Subcommands.Add(buildFibCommand);

        return experimentCommand;
    }
}
