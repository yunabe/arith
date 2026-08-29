using System.CommandLine;

namespace Arith.Cli.Tests;

public class VersionCommandTests
{
    [Fact]
    public void VersionCommand_PrintsDisplayVersion_AndReturnsZero()
    {
        StringWriter output = new();
        InvocationConfiguration configuration = new() { Output = output };

        int exitCode = Program.BuildRootCommand().Parse(["version"]).Invoke(configuration);

        Assert.Equal(0, exitCode);
        Assert.Equal(VersionInfo.DisplayVersion + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void DisplayVersion_LooksLikeSemanticVersion()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", VersionInfo.DisplayVersion);
    }

    [Fact]
    public void UnknownCommand_ReturnsNonZeroExitCode()
    {
        StringWriter output = new();
        StringWriter error = new();
        InvocationConfiguration configuration = new() { Output = output, Error = error };

        int exitCode = Program.BuildRootCommand().Parse(["no-such-command"]).Invoke(configuration);

        Assert.NotEqual(0, exitCode);
    }
}
