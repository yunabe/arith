using System.CommandLine;

namespace Arith.Cli.Tests;

internal static class CliRunner
{
    /// <summary>Runs the arith CLI in-process with captured output streams.</summary>
    internal static CliResult Run(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        InvocationConfiguration configuration = new() { Output = output, Error = error };

        int exitCode = Program.BuildRootCommand().Parse(args).Invoke(configuration);

        return new CliResult(exitCode, output.ToString(), error.ToString());
    }
}

internal sealed record CliResult(int ExitCode, string Output, string Error);
