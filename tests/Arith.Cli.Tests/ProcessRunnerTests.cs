using System.Diagnostics;

namespace Arith.Cli.Tests;

public class ProcessRunnerTests
{
    // Regression test for the classic redirected-pipe deadlock: a child that fills
    // the stderr pipe buffer before closing stdout hangs if the parent reads the
    // streams sequentially instead of draining them concurrently.
    [Fact]
    public void Run_DrainsLargeStderr_WithoutDeadlocking()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The test child process uses /bin/sh.");

        ProcessStartInfo startInfo = new("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        // Writes 1 MiB (far beyond the pipe buffer) to stderr, then exits 3.
        startInfo.ArgumentList.Add(
            "dd if=/dev/zero bs=1024 count=1024 2>/dev/null | tr '\\0' x >&2; exit 3");

        ProcessResult result = ProcessRunner.Run(startInfo);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(1024 * 1024, result.Error.Length);
    }
}
