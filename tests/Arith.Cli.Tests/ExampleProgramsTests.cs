using System.Runtime.CompilerServices;

namespace Arith.Cli.Tests;

/// <summary>
/// Compiles and runs every program in examples/, pinning the outputs the
/// examples README documents so the examples can never rot.
/// </summary>
public sealed class ExampleProgramsTests
{
    private static string ExamplePath(string name, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "examples", name));

    private static string[] Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    [Theory]
    [InlineData("fib.arith", new[] { "10" }, "fib(10) = 89")]
    [InlineData("factorial.arith", new[] { "20" }, "20! = 2432902008176640000")]
    // Correctness only: a depth safely inside any platform's default stack.
    // The deep-recursion behavior is the README's documented *manual*
    // experiment, because the stack-overflow boundary depends on the
    // platform's stack size and the JIT's tail-call heuristics.
    [InlineData("tailsum.arith", new[] { "1000" }, "sum(1..=1000) = 500500")]
    [InlineData("gcd.arith", new[] { "252", "105" }, "gcd(252, 105) = 21")]
    [InlineData("primes.arith", new[] { "30" }, "10 primes up to 30")]
    [InlineData("collatz.arith", new[] { "27" }, "collatz(27) reaches 1 after 111 steps")]
    [InlineData("pow.arith", new[] { "3", "13" }, "3^13 = 1594323")]
    [InlineData("pi.arith", new[] { "1000" }, "pi ~= 3.141592653340544  (1000 terms)")]
    public void Example_ProducesItsDocumentedFinalLine(string name, string[] arguments, string expectedLastLine)
    {
        CliResult result = CliRunner.Run(["run", ExamplePath(name), .. arguments]);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedLastLine, Lines(result.Output)[^1]);
    }

    [Fact]
    public void Fizzbuzz_PrintsTheClassicSequence()
    {
        CliResult result = CliRunner.Run("run", ExamplePath("fizzbuzz.arith"), "15");

        Assert.Equal(0, result.ExitCode);
        string[] expected =
        [
            "1", "2", "Fizz", "4", "Buzz", "Fizz", "7", "8",
            "Fizz", "Buzz", "11", "Fizz", "13", "14", "FizzBuzz",
        ];
        Assert.Equal(expected, Lines(result.Output));
    }

    [Fact]
    public void Primes_ListsEveryPrimeUpToThirty()
    {
        CliResult result = CliRunner.Run("run", ExamplePath("primes.arith"), "30");

        string[] expected =
        [
            "2", "3", "5", "7", "11", "13", "17", "19", "23", "29",
            "10 primes up to 30",
        ];
        Assert.Equal(expected, Lines(result.Output));
    }

    [Fact]
    public void Factorial_OfTwentyOne_FaultsOnCheckedOverflow()
    {
        // The README's checked-arithmetic demonstration: no wrapped answer.
        CliResult result = CliRunner.Run("run", ExamplePath("factorial.arith"), "21");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains("System.OverflowException", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Mandelbrot_RendersTheDocumentedFrame()
    {
        CliResult result = CliRunner.Run("run", ExamplePath("mandelbrot.arith"));

        Assert.Equal(0, result.ExitCode);
        string[] lines = Lines(result.Output);
        Assert.Equal(24, lines.Length);
        Assert.Equal(
            new string(' ', 31) + ".............++#@#++........",
            lines[3].TrimEnd());
    }
}
