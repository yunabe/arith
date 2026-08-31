using System.Diagnostics;

namespace Arith.Cli.Tests;

public class FibAotTests
{
    // NativeAOT compilation takes tens of seconds and needs a native linker, so
    // this test only runs when explicit tests are requested:
    //   dotnet test --project tests/Arith.Cli.Tests -- --explicit on
    [Fact(Explicit = true)]
    public void BuildFibCommand_Aot_ProducesRunnableNativeExecutable()
    {
        string outputDirectory = Directory.CreateTempSubdirectory("arith-fib-aot-test-").FullName;
        try
        {
            CliResult build = CliRunner.Run("experiment", "build-fib-command", outputDirectory, "--aot");
            Assert.Equal(0, build.ExitCode);

            string executable = Path.Combine(
                outputDirectory, OperatingSystem.IsWindows() ? "fib.exe" : "fib");
            Assert.True(File.Exists(executable));

            // The native binary runs directly — no dotnet host involved.
            ProcessStartInfo startInfo = new(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("10");
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the fib executable.");
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("fib(10) = 89\n", output.ReplaceLineEndings("\n"));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
