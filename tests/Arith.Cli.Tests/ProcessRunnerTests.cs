using System.Diagnostics;

namespace Arith.Cli.Tests;

public class ProcessRunnerTests
{
    // Regression test for the classic redirected-pipe deadlock: a child that fills
    // the stderr pipe buffer before closing stdout hangs if the parent reads the
    // streams sequentially instead of draining them concurrently.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_DrainsLargeStderr_WithoutDeadlocking(bool stream)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The test child process uses /bin/sh.");

        ProcessStartInfo startInfo = new("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        // Writes 1 MiB (far beyond the pipe buffer) to stderr, then exits 3.
        startInfo.ArgumentList.Add(
            "dd if=/dev/zero bs=1024 count=1024 2>/dev/null | tr '\\0' x >&2; exit 3");

        ProcessResult result;
        if (stream)
        {
            using StringWriter output = new();
            using StringWriter error = new();
            int exitCode = ProcessRunner.Run(startInfo, output, error);
            result = new ProcessResult(exitCode, output.ToString(), error.ToString());
        }
        else
        {
            result = ProcessRunner.Run(startInfo);
        }

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(1024 * 1024, result.Error.Length);
    }

    [Fact]
    public async Task Run_Streaming_ForwardsAndFlushesBothPipesBeforeExit()
    {
        string directory = Directory.CreateTempSubdirectory("arith-stream-test-").FullName;
        string release = Path.Combine(directory, "release");
        ProcessStartInfo startInfo = CreateWaitingChild(directory);
        using FlushedWriter output = new();
        using FlushedWriter error = new();
        Task<int> run = Task.Run(() => ProcessRunner.Run(startInfo, output, error),
            TestContext.Current.CancellationToken);

        try
        {
            // The child cannot exit until this test releases it. No newline
            // is written, so line-buffered forwarding would fail as well.
            Assert.Equal("out", await output.Flushed.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal("err", await error.Flushed.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            File.WriteAllText(release, "");
            try
            {
                // Cleanup must still release and reap the child if the
                // test's own cancellation token has already been canceled.
                await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        Assert.Equal(7, await run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_StreamingWriterFailure_TerminatesChild(bool failError)
    {
        string directory = Directory.CreateTempSubdirectory("arith-sink-test-").FullName;
        ProcessStartInfo startInfo = CreateWaitingChild(directory);
        using FailingWriter failing = new();
        Task<int> run = Task.Run(() => ProcessRunner.Run(startInfo,
            failError ? TextWriter.Null : failing, failError ? failing : TextWriter.Null),
            TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "release"), "");
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (IOException)
            {
                // The expected sink failure; the runner has reaped the child.
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ProcessStartInfo CreateWaitingChild(string directory)
    {
        string script = Path.Combine(directory, OperatingSystem.IsWindows() ? "child.cmd" : "child.sh");
        File.WriteAllText(script, OperatingSystem.IsWindows() ? """
            @echo off
            <nul set /p "=out"
            <nul set /p "=err" >&2
            :wait
            if exist "%~dp0release" exit /b 7
            goto wait
            """ : """
            printf out
            printf err >&2
            while [ ! -f "$1" ]; do sleep 0.05; done
            exit 7
            """);
        return OperatingSystem.IsWindows()
            ? new("cmd.exe", ["/d", "/c", script])
            : new("/bin/sh", [script, Path.Combine(directory, "release")]);
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("The output sink is closed."));
    }

    private sealed class FlushedWriter : StringWriter
    {
        public TaskCompletionSource<string> Flushed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task FlushAsync()
        {
            string text = ToString();
            if (text.Length >= 3)
            {
                Flushed.TrySetResult(text);
            }
            return Task.CompletedTask;
        }
    }
}
