namespace Arith.Cli;

internal static class CliApplication
{
    private const int UsageErrorExitCode = 2;

    public static int Run(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        string version)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (arguments.Count == 1 && arguments[0] == "version")
        {
            standardOutput.WriteLine($"arith {version}");
            return 0;
        }

        if (arguments.Count == 0)
        {
            standardError.WriteLine("error: no command was provided.");
        }
        else if (arguments[0] == "version")
        {
            standardError.WriteLine("error: the 'version' command does not accept arguments.");
        }
        else
        {
            standardError.WriteLine($"error: unknown command '{arguments[0]}'.");
        }

        standardError.WriteLine("usage: arith version");
        return UsageErrorExitCode;
    }
}
