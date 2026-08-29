namespace Arith.Cli.Tests;

[TestClass]
public sealed class CliApplicationTests
{
    private const string Version = "0.1.0";

    [TestMethod]
    public void VersionCommandPrintsProductVersion()
    {
        var result = Run("version");

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual($"arith {Version}{Environment.NewLine}", result.StandardOutput);
        Assert.AreEqual(string.Empty, result.StandardError);
    }

    [TestMethod]
    public void MissingCommandReturnsUsageError()
    {
        var result = Run();

        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardOutput);
        Assert.AreEqual(
            $"error: no command was provided.{Environment.NewLine}" +
            $"usage: arith version{Environment.NewLine}",
            result.StandardError);
    }

    [TestMethod]
    public void UnknownCommandReturnsUsageError()
    {
        var result = Run("compile");

        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardOutput);
        Assert.AreEqual(
            $"error: unknown command 'compile'.{Environment.NewLine}" +
            $"usage: arith version{Environment.NewLine}",
            result.StandardError);
    }

    [TestMethod]
    public void VersionCommandRejectsAdditionalArguments()
    {
        var result = Run("version", "extra");

        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardOutput);
        Assert.AreEqual(
            $"error: the 'version' command does not accept arguments.{Environment.NewLine}" +
            $"usage: arith version{Environment.NewLine}",
            result.StandardError);
    }

    private static CliResult Run(params string[] arguments)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = CliApplication.Run(
            arguments,
            standardOutput,
            standardError,
            Version);

        return new CliResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
