using System.Buffers.Binary;
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
            AssertIsNativeExecutableImage(executable);

            // The native binary runs directly — no dotnet host involved.
            ProcessStartInfo startInfo = new(executable);
            startInfo.ArgumentList.Add("10");
            ProcessResult run = ProcessRunner.Run(startInfo);

            Assert.Equal(0, run.ExitCode);
            Assert.Equal("fib(10) = 89\n", run.Output.ReplaceLineEndings("\n"));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Guards against regressing to the non-AOT output: the shell launcher script
    /// would also "run directly", so require a native image header (Mach-O / ELF /
    /// PE) rather than a `#!` script.
    /// </summary>
    private static void AssertIsNativeExecutableImage(string path)
    {
        byte[] header = new byte[4];
        using (FileStream stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        bool isNativeImage =
            (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            || (header[0] == (byte)'M' && header[1] == (byte)'Z')
            || magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE // Mach-O (both endiannesses)
            || magic is 0xCAFEBABE or 0xBEBAFECA;                            // Mach-O universal binary

        Assert.True(isNativeImage, $"'{path}' does not start with a native executable image header.");
    }
}
