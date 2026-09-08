using System.Diagnostics;

namespace Arith.Cli;

internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion with captured output, for callers such
    /// as NativeAOT publishing that need to inspect the finished log.
    /// </summary>
    internal static ProcessResult Run(ProcessStartInfo startInfo)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = Run(startInfo, output, error);
        return new ProcessResult(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Forwards both pipes concurrently as output arrives, using bounded
    /// buffers. Reading either pipe to EOF first can deadlock the child.
    /// </summary>
    internal static int Run(ProcessStartInfo startInfo, TextWriter output, TextWriter error)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        Task outputTask = ForwardAsync(process.StandardOutput, output, process);
        Task errorTask = ForwardAsync(process.StandardError, error, process);
        process.WaitForExit();
        Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static async Task ForwardAsync(TextReader source, TextWriter destination, Process process)
    {
        try
        {
            char[] buffer = new char[4096];
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // A failed output sink must not leave the child blocked on a
            // pipe that is no longer being drained (e.g. arith run | head).
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child already exited.
            }

            throw;
        }
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
