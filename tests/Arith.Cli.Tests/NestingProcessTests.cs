using System.Diagnostics;

using Arith.Compiler.Syntax;

namespace Arith.Cli.Tests;

/// <summary>
/// Very long and very deeply nested programs, compiled by `arith run` in a
/// child process (issue #34: a flat 15,000-term sum overflowed the
/// compiler's stack). A stack overflow kills its process outright, so these
/// run out of process — a regression fails one test instead of taking the
/// whole test runner down. The limit itself and the recovered trees are
/// pinned in Arith.Compiler.Tests.NestingLimitTests.
/// </summary>
public sealed class NestingProcessTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("arith-nesting-test-").FullName;

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

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static string Chain(string term, string op, int count) =>
        string.Join($" {op} ", Enumerable.Repeat(term, count));

    private static string[] Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    private string WriteSource(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Runs `arith run` on the source in a child dotnet process, using the
    /// CLI assembly deployed next to the tests. With a stack size the child
    /// is started through /bin/sh so `ulimit -s` (KiB) shrinks its main
    /// thread's stack — the thread the CLI compiles on.
    /// </summary>
    private static ProcessResult RunOutOfProcess(string sourcePath, int? stackKilobytes = null)
    {
        string cli = Path.Combine(AppContext.BaseDirectory, "arith.dll");
        ProcessStartInfo startInfo;
        if (stackKilobytes is { } kilobytes)
        {
            startInfo = new("/bin/sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"ulimit -s {kilobytes} && exec dotnet \"$0\" run \"$1\"");
            startInfo.ArgumentList.Add(cli);
            startInfo.ArgumentList.Add(sourcePath);
        }
        else
        {
            startInfo = new("dotnet");
            startInfo.ArgumentList.Add(cli);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(sourcePath);
        }

        return ProcessRunner.Run(startInfo);
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("products")]
    [InlineData("logical")]
    [InlineData("strings")]
    [InlineData("comparison")]
    [InlineData("interpolation")]
    [InlineData("annotated")]
    public void FlatChain_CompilesAndRuns(string shape)
    {
        // The issue's reproduction is "sum": 15,000 terms, expected 15000.
        const int terms = 15_000;
        (string body, string expected) = shape switch
        {
            "sum" => ($"print({Chain("1", "+", terms)});", "15000"),
            "products" => ($"print({Chain("1 * 2", "+", terms)});", "30000"),
            "logical" => ($"print({Chain("true", "&&", terms)} || false);", "true"),
            "strings" => ($"let s = {Chain("\"a\"", "+", terms)}; print(s == \"{new string('a', terms)}\");", "true"),
            "comparison" => ($"print({Chain("1", "+", terms)} < {Chain("1", "+", terms)} + 1);", "true"),
            "interpolation" => ($"let x = 7; print(f\"{Repeat("${x}", terms)}\" == \"{new string('7', terms)}\");", "true"),
            "annotated" => ($"let n: i32 = {Chain("1", "+", terms)}; print(n - 1);", "14999"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        string source = WriteSource("flat.arith", $"fn main() {{ {body} }}\n");

        ProcessResult result = RunOutOfProcess(source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal([expected], Lines(result.Output));
    }

    [Theory]
    [InlineData("parentheses")]
    [InlineData("unary")]
    [InlineData("right-nested")]
    [InlineData("calls")]
    [InlineData("arrays")]
    [InlineData("index-chain")]
    [InlineData("if-statements")]
    [InlineData("else-if-chain")]
    [InlineData("interpolated-strings")]
    public void ExcessiveNesting_ReportsADiagnosticInsteadOfCrashing(string shape)
    {
        const int depth = 20_000;
        string body = shape switch
        {
            "parentheses" => $"print({Repeat("(", depth)}1{Repeat(")", depth)});",
            "unary" => $"print({Repeat("-", depth)}1);",
            "right-nested" => $"print({Repeat("1 - (", depth)}1{Repeat(")", depth)});",
            "calls" => $"print({Repeat("i64(", depth)}1{Repeat(")", depth)});",
            "arrays" => $"let a = {Repeat("[", depth)}1{Repeat("]", depth)}; print(1);",
            "index-chain" => $"let a = [1]; print(a{Repeat("[0]", depth)});",
            "if-statements" => $"{Repeat("if true {{ ", depth)}print(1);{Repeat(" }}", depth)}",
            "else-if-chain" => "let x = 5; if x == 0 { print(0); } "
                + string.Join(" ", Enumerable.Range(1, depth).Select(i => $"else if x == {i} {{ print({i}); }}")),
            "interpolated-strings" => $"print({Repeat("f\"${", depth)}1{Repeat("}\"", depth)});",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        string source = WriteSource("deep.arith", $"fn main() {{\n    {body}\n}}\n");

        ProcessResult result = RunOutOfProcess(source);

        Assert.DoesNotContain("Stack overflow", result.Error, StringComparison.Ordinal);
        string diagnostic = Assert.Single(Lines(result.Error), line => line.Contains("ARITH2005", StringComparison.Ordinal));
        Assert.StartsWith($"{source}:2:", diagnostic, StringComparison.Ordinal);
        Assert.EndsWith(
            "error ARITH2005: nesting is too deep: the compiler supports at most "
            + $"{SyntaxFacts.MaxNestingDepth} levels of nested expressions and statements",
            diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public void DeepestAllowedNesting_CompilesAndRunsOnAOneMebibyteStack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shrinks the child's main-thread stack with /bin/sh and ulimit.");

        // Every nesting shape at the deepest depth the limit admits, in one
        // program. Each stage's recursion is proportional to that depth, so
        // this pins the headroom the limit was chosen for: 1 MiB is the
        // main-thread stack on Windows (Unix defaults to 8 MiB, which would
        // hide a regression), and tests build the compiler in Debug, whose
        // frames are the largest.
        int depth = SyntaxFacts.MaxNestingDepth - 3; // Levels spent by `print(…);` around its argument.
        int wraps = depth - 1;                       // Each `[` is a level and its index another.
        int arms = SyntaxFacts.MaxNestingDepth - 4;  // An `else if` arm's print argument sits 4 levels below it.
        string source = WriteSource("deepest.arith", string.Join("\n",
            "fn id(x: i64) -> i64 { return x; }",
            "fn main() {",
            $"    print({Repeat("(", depth)}1{Repeat(")", depth)});",
            $"    print({Repeat("-", depth)}2);",
            $"    print({Repeat("id(", depth)}3{Repeat(")", depth)});",
            $"    print({Repeat("f\"${", depth)}4{Repeat("}\"", depth)});",
            $"    let a = {Repeat("[", wraps)}5{Repeat("]", wraps)}; print(a{Repeat("[0]", wraps)});",
            $"    {Repeat("if true { ", depth)}print(6);{Repeat(" }", depth)}",
            $"    let x = {arms}; if x == 0 {{ print(0); }} "
                + string.Join(" ", Enumerable.Range(1, arms).Select(i => $"else if x == {i} {{ print({i}); }}")),
            "}",
            ""));

        ProcessResult result = RunOutOfProcess(source, stackKilobytes: 1024);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["1", "-2", "3", "4", "5", "6", arms.ToString(System.Globalization.CultureInfo.InvariantCulture)], Lines(result.Output));
    }
}
