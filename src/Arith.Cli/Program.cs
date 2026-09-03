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
        rootCommand.Subcommands.Add(BuildBuildCommand());
        rootCommand.Subcommands.Add(BuildRunCommand());

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
        Option<bool> aotOption = new("--aot")
        {
            Description = "Compile ahead-of-time into a single native executable "
                + "(requires the platform's native linker; no dotnet host needed to run it).",
        };
        Command buildCommand = new("build")
        {
            Description = "Compile an Arith source file into a .NET assembly.",
        };
        buildCommand.Arguments.Add(sourceArgument);
        buildCommand.Options.Add(outputOption);
        buildCommand.Options.Add(aotOption);
        buildCommand.SetAction(parseResult => CompilerCommands.Build(
            parseResult.GetRequiredValue(sourceArgument),
            parseResult.GetValue(outputOption),
            parseResult.GetValue(aotOption),
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
        Argument<string[]> programArguments = new("arguments")
        {
            Description = "Arguments passed to the program's main "
                + "(use `--` before values that start with '-').",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command runCommand = new("run")
        {
            Description = "Compile and run an Arith source file, forwarding its exit code.",
        };
        runCommand.Arguments.Add(sourceArgument);
        runCommand.Arguments.Add(programArguments);
        runCommand.SetAction(parseResult => CompilerCommands.Run(
            parseResult.GetRequiredValue(sourceArgument),
            parseResult.GetValue(programArguments) ?? [],
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error));
        return runCommand;
    }
}
