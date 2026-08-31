using System.Diagnostics;

namespace Arith.Cli.Tests;

/// <summary>
/// Emits the fib program once via `arith experiment build-fib-command` and lets the
/// tests in <see cref="FibCommandTests"/> run the produced assembly out of process.
/// </summary>
public sealed class FibProgramFixture : IDisposable
{
    public FibProgramFixture()
    {
        OutputDirectory = Directory.CreateTempSubdirectory("arith-fib-test-").FullName;
        CliResult result = CliRunner.Run("experiment", "build-fib-command", OutputDirectory);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"build-fib-command failed (exit {result.ExitCode}): {result.Error}");
        }
    }

    public string OutputDirectory { get; }

    public void Dispose() => Directory.Delete(OutputDirectory, recursive: true);
}

public class FibCommandTests : IClassFixture<FibProgramFixture>
{
    private readonly FibProgramFixture _fixture;

    public FibCommandTests(FibProgramFixture fixture) => _fixture = fixture;

    [Fact]
    public void BuildFibCommand_WritesExpectedFiles()
    {
        Assert.True(File.Exists(Path.Combine(_fixture.OutputDirectory, "fib.dll")));
        Assert.True(File.Exists(Path.Combine(_fixture.OutputDirectory, "fib.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(_fixture.OutputDirectory, "fib")));
        Assert.True(File.Exists(Path.Combine(_fixture.OutputDirectory, "fib.cmd")));
    }

    [Theory]
    [InlineData("-1", 1L)]
    [InlineData("0", 1L)]
    [InlineData("1", 1L)]
    [InlineData("2", 2L)]
    [InlineData("10", 89L)]
    [InlineData("30", 1346269L)]
    public void EmittedFib_ComputesRecursiveFibonacci(string argument, long expected)
    {
        ProcessResult result = RunFib(argument);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"fib({argument}) = {expected}\n", result.Output.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void EmittedFib_WithoutArguments_PrintsUsageAndFails()
    {
        ProcessResult result = RunFib();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("usage: fib <n>", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmittedFib_WithNonNumericArgument_PrintsUsageAndFails()
    {
        ProcessResult result = RunFib("abc");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("usage: fib <n>", result.Error, StringComparison.Ordinal);
    }

    private ProcessResult RunFib(params string[] args)
    {
        ProcessStartInfo startInfo = new("dotnet");
        startInfo.ArgumentList.Add(Path.Combine(_fixture.OutputDirectory, "fib.dll"));
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return ProcessRunner.Run(startInfo);
    }
}
