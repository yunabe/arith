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

    [Theory]
    [InlineData("print(10 / zero());")]
    [InlineData("print(10 % zero());")] // Spec §11: remainder by zero faults too.
    public void Run_DivisionOrRemainderByZero_FailsAtRuntime(string statement)
    {
        string source = WriteSource("divzero.arith", $$"""
            fn zero() -> i64 {
                return 0;
            }

            fn main() {
                {{statement}}
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("System.DivideByZeroException", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_DividingI64MinByMinusOne_Overflows()
    {
        // Spec §11: integer division is checked; i64::MIN / -1 has no
        // representable result.
        string source = WriteSource("divmin.arith", """
            fn minusOne() -> i64 {
                return -1;
            }

            fn main() {
                print(-9223372036854775808 / minusOne());
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("System.OverflowException", result.Error, StringComparison.Ordinal);
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
    public void Run_ReadmeExampleProgram_ProducesTheDocumentedOutput()
    {
        string source = WriteSource("example.arith", """
            fn sum_range(start: i64, end: i64) -> i64 {
                let total = 0;

                for i in start..end {
                    total += i;
                }

                return total;
            }

            fn main() -> i32 {
                let result = sum_range(1, 11);

                if result > 50 {
                    print("large:");
                    print(result);
                } else {
                    print("small:");
                    print(result);
                }

                return 0;
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["large:", "55"], Lines(result.Output));
    }

    [Fact]
    public void Run_RecursiveFibonacci_Terminates()
    {
        string source = WriteSource("fib.arith", """
            fn fib(n: i64) -> i64 {
                if n < 2 {
                    return 1;
                }
                return fib(n - 1) + fib(n - 2);
            }

            fn main() {
                print(fib(10));
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["89"], Lines(result.Output));
    }

    [Fact]
    public void Run_WhileWithBreakAndContinue_LoopsCorrectly()
    {
        string source = WriteSource("loops.arith", """
            fn main() {
                let i = 0;
                while true {
                    i += 1;
                    if i > 6 {
                        break;
                    }
                    if i % 2 == 0 {
                        continue;
                    }
                    print(i);
                }
                print("done");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["1", "3", "5", "done"], Lines(result.Output));
    }

    [Fact]
    public void Run_ClosedRangeAtI64Max_RunsThreeIterationsAndTerminates()
    {
        // The design §4.5 endpoint rule end to end: a closed range ending at
        // i64::MAX must not increment past the endpoint.
        string source = WriteSource("maxrange.arith", """
            fn main() {
                for i in (9223372036854775807 - 2)..=9223372036854775807 {
                    print(i - 9223372036854775805);
                }
                print("survived");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["0", "1", "2", "survived"], Lines(result.Output));
    }

    [Fact]
    public void Run_EmptyAndInclusiveRanges_IterateThePromisedCounts()
    {
        string source = WriteSource("ranges.arith", """
            fn main() {
                for i in 5..5 { print(i); }
                for i in 7..3 { print(i); }
                for i in 0..=2 { print(i); }
                print("end");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["0", "1", "2", "end"], Lines(result.Output));
    }

    [Fact]
    public void Run_ShortCircuit_SkipsTheRightOperand()
    {
        // Spec §8.3: && and || short-circuit; side() must not run.
        string source = WriteSource("shortcircuit.arith", """
            fn side() -> bool {
                print("side effect");
                return true;
            }

            fn main() {
                print(false && side());
                print(true || side());
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["False", "True"], Lines(result.Output));
    }

    [Fact]
    public void Run_FloatComparisons_FollowIeee754()
    {
        // Spec §8.2: NaN compares false with everything, including itself.
        string source = WriteSource("nan.arith", """
            fn main() {
                let nan = 0.0 / 0.0;
                print(nan);
                print(nan == nan);
                print(nan <= nan);
                print(1.5 <= 1.5);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["NaN", "False", "False", "True"], Lines(result.Output));
    }

    [Fact]
    public void Run_StringEquality_ComparesContents()
    {
        // Behavioral smoke test only: every string here comes from an
        // interned ldstr, so this run could not tell string.Equals from
        // reference equality. EmitterTests.StringEquality_CallsStringEquals
        // pins the actual lowering by inspecting the IL.
        string source = WriteSource("streq.arith", """
            fn label(flag: bool) -> string {
                if flag {
                    return "yes";
                }
                return "no";
            }

            fn main() {
                print(label(true) == "yes");
                print(label(false) != "no");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["True", "False"], Lines(result.Output));
    }

    [Fact]
    public void Run_Conversions_MatchTheSpecExamples()
    {
        string source = WriteSource("convert.arith", """
            fn main() {
                let small: i32 = 10;
                let large = i64(small);
                let value = f64(large) / 4.0;
                print(value);
                print(i64(1.9));
                print(i32(-7));
                let a = 5 / 2;
                let message = "answer=" + string(a);
                print(message);
                print(string(true) + "/" + string(1.5f32));
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["2.5", "1", "-7", "answer=2", "True/1.5"], Lines(result.Output));
    }

    [Theory]
    [InlineData("print(i32(3000000000));")]  // Narrowing out of range (spec §7).
    [InlineData("print(i64(0.0 / 0.0));")]   // NaN to integer.
    [InlineData("print(i64(1.0 / 0.0));")]   // Infinity to integer.
    public void Run_InvalidRuntimeConversion_FailsWithOverflow(string statement)
    {
        string source = WriteSource("badconv.arith", $"fn main() {{ {statement} }}");

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("System.OverflowException", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_StringEquality_OnRuntimeBuiltStrings()
    {
        // string(1) + "x" is built at runtime by String.Concat, so it cannot
        // be reference-equal to the interned literal — this run genuinely
        // distinguishes content equality from ceq.
        string source = WriteSource("runtimestreq.arith", """
            fn main() {
                print(string(1) + "x" == "1x");
                print(string(1) + "x" != "1x");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["True", "False"], Lines(result.Output));
    }

    [Fact]
    public void Run_CompoundConcatenation_BuildsAString()
    {
        string source = WriteSource("concat.arith", """
            fn main() {
                let s = "";
                for i in 1..=3 {
                    s += string(i);
                }
                print(s);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["123"], Lines(result.Output));
    }

    [Fact]
    public void Run_TypedMain_ReceivesParsedArguments()
    {
        // Spec §5.1: main parameters receive command-line arguments parsed
        // per type with the invariant culture.
        string source = WriteSource("typed.arith", """
            fn main(count: i64, label: string, scale: f64, loud: bool) -> i32 {
                for i in 0..count {
                    print(label + " " + string(f64(i + 1) * scale));
                }
                if loud {
                    print("!!!");
                }
                return i32(count);
            }
            """);

        CliResult result = CliRunner.Run("run", source, "3", "hey", "1.5", "true");

        Assert.Equal("", result.Error);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(["hey 1.5", "hey 3", "hey 4.5", "!!!"], Lines(result.Output));
    }

    [Theory]
    [InlineData]                       // Too few arguments.
    [InlineData("1", "2")]             // Too many.
    [InlineData("abc")]                // Not an i64.
    [InlineData("1.5")]                // A float is not an i64.
    [InlineData("9223372036854775808")] // Out of i64 range.
    public void Run_TypedMainWithBadArguments_PrintsUsageAndExits2(params string[] arguments)
    {
        string source = WriteSource("usage.arith", "fn main(n: i64) { print(n); }");

        CliResult result = CliRunner.Run(["run", source, .. arguments]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal(["usage: usage <n: i64>"], Lines(result.Error));
    }

    [Fact]
    public void Run_NegativeArgument_PassesThroughAfterDoubleDash()
    {
        string source = WriteSource("neg.arith", "fn main(n: i64) { print(n * 2); }");

        CliResult result = CliRunner.Run("run", source, "--", "-21");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["-42"], Lines(result.Output));
    }

    [Fact]
    public void Run_BoolArgument_IsCaseInsensitive()
    {
        string source = WriteSource("boolarg.arith", "fn main(flag: bool) { print(flag); }");

        CliResult result = CliRunner.Run("run", source, "TRUE");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["True"], Lines(result.Output));
    }

    [Fact]
    public void Build_TypedMain_LauncherForwardsArguments()
    {
        string source = WriteSource("echoarg.arith", "fn main(s: string) { print(s); }");
        string outputDirectory = Path.Combine(_directory, "out");

        CliResult build = CliRunner.Run("build", source, "-o", outputDirectory);
        Assert.Equal(0, build.ExitCode);

        if (!OperatingSystem.IsWindows())
        {
            ProcessStartInfo startInfo = new(Path.Combine(outputDirectory, "echoarg"));
            startInfo.ArgumentList.Add("via launcher");
            ProcessResult run = ProcessRunner.Run(startInfo);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal(["via launcher"], Lines(run.Output));
        }
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
