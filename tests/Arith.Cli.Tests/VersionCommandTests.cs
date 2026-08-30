using System.CommandLine;

namespace Arith.Cli.Tests;

public class VersionCommandTests
{
    [Fact]
    public void VersionCommand_PrintsDisplayVersion_AndReturnsZero()
    {
        CliResult result = RunCli("version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(VersionInfo.DisplayVersion + Environment.NewLine, result.Output);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void DisplayVersion_LooksLikeSemanticVersion()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", VersionInfo.DisplayVersion);
    }

    [Fact]
    public void NoCommand_Fails_WithoutPrintingVersion()
    {
        CliResult result = RunCli();

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(VersionInfo.DisplayVersion, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCommand_Fails_WithoutPrintingVersion()
    {
        CliResult result = RunCli("no-such-command");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(VersionInfo.DisplayVersion, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionCommand_RejectsExtraArguments()
    {
        CliResult result = RunCli("version", "extra");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(VersionInfo.DisplayVersion, result.Output, StringComparison.Ordinal);
    }

    private static CliResult RunCli(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        InvocationConfiguration configuration = new() { Output = output, Error = error };

        int exitCode = Program.BuildRootCommand().Parse(args).Invoke(configuration);

        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
