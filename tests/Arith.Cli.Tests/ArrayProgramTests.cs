namespace Arith.Cli.Tests;

/// <summary>
/// End-to-end tests for the v0.2 array core (LANGUAGE_SPEC §3.1, §4.5, §8.4,
/// §8.6, §10.2): creation forms, indexing, element writes, len, reference
/// semantics, and the specified runtime faults.
/// </summary>
public sealed class ArrayProgramTests : IDisposable
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
    public void Run_ArrayCoreProgram_CreatesIndexesAndMutates()
    {
        string source = WriteSource("arrays.arith", """
            fn sum(values: []i64) -> i64 {
                let total = 0;
                for i in 0..len(values) {
                    total += values[i];
                }
                return total;
            }

            fn main() {
                let primes = [2, 3, 5, 7];
                print(len(primes));
                print(primes[0]);
                print(primes[len(primes) - 1]);
                primes[1] = 30;
                primes[2] += 10;
                print(sum(primes));

                let zeros = [0; 3];
                print(sum(zeros));
                let empty: []i64 = [];
                print(len(empty));
                print(len([true; 0]));

                let floats: []f32 = [0.5, 1.5];
                print(floats[0] + floats[1]);
                let names = ["ab", "cd"];
                print(names[0] + names[1]);
                let flags = [false; 2];
                flags[1] = true;
                print(flags[0]);
                print(flags[1]);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        string[] expected =
        [
            "4", "2", "7", "54",
            "0", "0", "0",
            "2", "abcd", "false", "true",
        ];
        Assert.Equal(expected, Lines(result.Output));
    }

    [Fact]
    public void Run_ArraysAreReferences_WritesAreSharedAndRepeatSharesOneRow()
    {
        // Spec §3.1: assignment and argument passing share the one array.
        // Spec §4.5: `[[0; 3]; 2]` evaluates the row once, so both slots
        // alias it until reassigned in a loop.
        string source = WriteSource("aliasing.arith", """
            fn bump(values: []i64) {
                values[0] = 99;
            }

            fn main() {
                let a = [1, 2];
                let b = a;
                b[0] = 10;
                print(a[0]);
                bump(a);
                print(a[0]);

                let grid = [[0; 3]; 2];
                grid[0][0] = 7;
                print(grid[1][0]);
                for r in 0..len(grid) {
                    grid[r] = [0; 3];
                }
                grid[0][0] = 7;
                print(grid[1][0]);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["10", "99", "7", "0"], Lines(result.Output));
    }

    [Fact]
    public void Run_CallRootedElementWrite_MutatesTheReturnedArray()
    {
        // Spec §8.4: an assignment target may be rooted at any postfix
        // expression; a write through a call result reaches the array the
        // call returned, while a write into a fresh literal is legal but
        // unobservable.
        string source = WriteSource("call-rooted.arith", """
            fn identity(values: []i64) -> []i64 {
                return values;
            }

            fn main() {
                let a = [1, 2];
                identity(a)[0] = 9;
                identity(a)[1] += 5;
                print(a[0]);
                print(a[1]);
                [1, 2][0] = 42;
                print("done");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["9", "7", "done"], Lines(result.Output));
    }

    [Fact]
    public void Run_NestedArrays_AreIndependentJaggedRows()
    {
        string source = WriteSource("jagged.arith", """
            fn rows() -> [][]i64 {
                return [[1], [2, 3], []];
            }

            fn main() {
                let grid = rows();
                print(len(grid));
                print(len(grid[1]));
                print(len(grid[2]));
                print(grid[1][1]);
                grid[0] = [4, 5, 6];
                print(grid[0][2] + grid[1][0]);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["3", "2", "0", "3", "8"], Lines(result.Output));
    }

    [Fact]
    public void Run_RepeatForm_EvaluatesValueOnceThenCount()
    {
        // Spec §4.5: the value is evaluated once, the count once, after it.
        string source = WriteSource("repeat-order.arith", """
            fn note(label: string, v: i64) -> i64 {
                print(label);
                return v;
            }

            fn main() {
                let a = [note("value", 7); note("count", 2)];
                print(a[0] + a[1]);
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["value", "count", "14"], Lines(result.Output));
    }

    [Fact]
    public void Run_ForEach_VisitsElementsInOrderWithControlFlow()
    {
        // Spec §9.3: elements visit in index order, the element is read when
        // the iteration starts (so writes are visible to later iterations),
        // and break/continue behave as in the range loop.
        string source = WriteSource("foreach.arith", """
            fn main() {
                let values = [10, 20, 30];
                let total = 0;
                for value in values {
                    total += value;
                }
                print(total);

                for value in values {
                    values[2] = 99;
                    print(value);
                }

                for row in [[1, 2], [3]] {
                    print(len(row));
                }

                let empty: []i64 = [];
                for x in empty {
                    print(x);
                }
                for x in [1, 2, 3, 4] {
                    if x == 2 {
                        continue;
                    }
                    if x == 4 {
                        break;
                    }
                    print(x);
                }
                print("done");
            }
            """);

        CliResult result = CliRunner.Run("run", source);

        Assert.Equal("", result.Error);
        Assert.Equal(0, result.ExitCode);
        string[] expected =
        [
            "60",
            "10", "20", "99",
            "2", "1",
            "1", "3", "done",
        ];
        Assert.Equal(expected, Lines(result.Output));
    }

    [Theory]
    [InlineData("let a = [1, 2]; print(a[2]);", "System.IndexOutOfRangeException")]
    [InlineData("let a = [1, 2]; print(a[-1]);", "System.IndexOutOfRangeException")]
    [InlineData("let a = [1, 2]; a[2] = 0;", "System.IndexOutOfRangeException")]
    [InlineData("let n = 0 - 1; let a = [0; n]; print(len(a));", "System.OverflowException")]
    public void Run_ArrayRuntimeFault_FailsWithTheSpecifiedException(string body, string exception)
    {
        // Spec §8.6/§4.5: out-of-range indexes and a negative repeat count
        // are runtime errors.
        string source = WriteSource("fault.arith", $"fn main() {{ {body} }}");

        CliResult result = CliRunner.Run("run", source);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(exception, result.Error, StringComparison.Ordinal);
    }
}
