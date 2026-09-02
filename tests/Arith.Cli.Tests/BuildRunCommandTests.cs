using System.Diagnostics;

namespace Arith.Cli.Tests;

/// <summary>
/// End-to-end tests for `arith build` / `arith run`: compile real Arith
/// source, execute the emitted assembly with the dotnet host, and assert
/// stdout and exit codes.
/// </summary>
public sealed class BuildRunCommandTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("arith-cli-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string WriteSource(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string[] Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    [Fact]
    public void Run_SubsetProgram_PrintsEveryValueKind()
    {
        string source = WriteSource("values.arith", """
            fn add(a: i64, b: i64) -> i64 {
                return a + b;
            }

            fn main() -> i32 {
                let total = add(20, 22);
                print(total);
                print("answer above");
                print(3.5);
                print(1.5f32);
                print(true);
                print(-total);
                print(-9223372036854775808);
                let small: i32 = 7;
                print(small % 3);
                let counter = 10;
                counter += 5;
                counter *= 2;
                print(counter);
                return 0;
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        string[] expected =
        [
            "42", "answer above", "3.5", "1.5", "True",
            "-42", "-9223372036854775808", "1", "30",
        ];
        Assert.Equal(expected, Lines(result.Output));
    }

    [Fact]
    public void Run_MainReturnValue_BecomesTheExitCode()
    {
        string source = WriteSource("exit7.arith", "fn main() -> i32 { return 7; }");

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public void Run_VoidMain_ExitsZero()
    {
        string source = WriteSource("voidmain.arith", "fn main() { print(\"ok\"); }");

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["ok"], Lines(result.Output));
    }

    [Fact]
    public void Run_ArgumentsAndOperands_EvaluateLeftToRight()
    {
        // Spec §11: subexpressions evaluate left to right.
        string source = WriteSource("order.arith", """
            fn first() -> i64 {
                print("first");
                return 1;
            }

            fn second() -> i64 {
                print("second");
                return 2;
            }

            fn main() {
                print(first() + second());
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["first", "second", "3"], Lines(result.Output));
    }

    [Fact]
    public void Run_IntegerOverflow_FailsAtRuntime()
    {
        // Spec §11: integer arithmetic is checked.
        string source = WriteSource("overflow.arith", """
            fn big() -> i64 {
                return 9223372036854775807;
            }

            fn main() {
                print(big() + 1);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("System.OverflowException", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_DivisionByZero_FailsAtRuntime()
    {
        string source = WriteSource("divzero.arith", """
            fn zero() -> i64 {
                return 0;
            }

            fn main() {
                print(10 / zero());
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("System.DivideByZeroException", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_CheckedNegation_FailsOnI64MinValue()
    {
        // -(i64 min) has no i64 representation; spec §11 makes negation checked.
        string source = WriteSource("negate.arith", """
            fn min() -> i64 {
                return -9223372036854775808;
            }

            fn main() {
                print(-min());
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("System.OverflowException", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WritesRunnableArtifacts()
    {
        string source = WriteSource("hello.arith", "fn main() { print(\"hello from arith\"); }");
        string outputDirectory = Path.Combine(_directory, "out");

        CliResult result = CliRunner.Run("build", source, "-o", outputDirectory);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        string assemblyPath = Path.Combine(outputDirectory, "hello.dll");
        Assert.True(File.Exists(assemblyPath));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "hello.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "hello")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "hello.cmd")));

        // The build output runs with the plain dotnet host.
        ProcessResult run = ProcessRunner.Run(new ProcessStartInfo("dotnet", [assemblyPath]));
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(["hello from arith"], Lines(run.Output));

        // The POSIX launcher derives the dll path from its own location, so
        // it works from any working directory.
        if (!OperatingSystem.IsWindows())
        {
            ProcessResult launched = ProcessRunner.Run(
                new ProcessStartInfo(Path.Combine(outputDirectory, "hello")) { WorkingDirectory = _directory });
            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(["hello from arith"], Lines(launched.Output));
        }
    }

    [Theory]
    [InlineData("bad name.arith")]     // Space.
    [InlineData("1st.arith")]          // Leading digit.
    [InlineData("hello.txt")]          // Wrong extension.
    [InlineData("hello.arith.arith")]  // Dot in the program name.
    public void Build_InvalidSourceFileName_FailsWithTheNamingRule(string fileName)
    {
        string source = WriteSource(fileName, "fn main() { }");

        CliResult result = CliRunner.Run("build", source, "-o", Path.Combine(_directory, "out"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not a valid source file name", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_directory, "out")));
    }

    [Fact]
    public void Build_OutputDirectoryIsAFile_FailsWithoutAStackTrace()
    {
        // The reviewer's case: `arith build prog.arith -o <existing file>`
        // must produce a concise CLI error, not an unhandled IOException.
        string source = WriteSource("prog.arith", "fn main() { }");
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "");

        CliResult result = CliRunner.Run("build", source, "-o", blocker);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot write artifacts to", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_HyphenatedProgramName_IsAccepted()
    {
        string source = WriteSource("my-app_2.arith", "fn main() { print(\"ok\"); }");

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["ok"], Lines(result.Output));
    }

    [Fact]
    public void Build_InvalidProgram_PrintsDiagnosticsAndWritesNothing()
    {
        string source = WriteSource("bad.arith", """
            fn main() {
                let x: i32 = 3000000000;
                print(y);
            }
            """);
        string outputDirectory = Path.Combine(_directory, "out");

        CliResult result = CliRunner.Run("build", source, "-o", outputDirectory);

        Assert.Equal(1, result.ExitCode);
        string[] errors = Lines(result.Error);
        Assert.Equal(2, errors.Length);
        Assert.Equal($"{source}:2:18: error ARITH3012: integer literal '3000000000' is out of range for type 'i32'", errors[0]);
        Assert.Equal($"{source}:3:11: error ARITH3005: 'y' is not defined", errors[1]);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public void Run_MissingSourceFile_FailsWithMessage()
    {
        CliResult result = CliRunner.Run("run", Path.Combine(_directory, "nope.arith"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot read", result.Error, StringComparison.Ordinal);
    }
}
