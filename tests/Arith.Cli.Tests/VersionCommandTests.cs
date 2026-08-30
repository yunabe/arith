namespace Arith.Cli.Tests;

public class VersionCommandTests
{
    [Fact]
    public void VersionCommand_PrintsDisplayVersion_AndReturnsZero()
    {
        CliResult result = CliRunner.Run("version");

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
        CliResult result = CliRunner.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(VersionInfo.DisplayVersion, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCommand_Fails_WithoutPrintingVersion()
    {
        CliResult result = CliRunner.Run("no-such-command");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(VersionInfo.DisplayVersion, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionCommand_RejectsExtraArguments()
    {
        CliResult result = CliRunner.Run("version", "extra");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(VersionInfo.DisplayVersion, result.Output, StringComparison.Ordinal);
    }
}
